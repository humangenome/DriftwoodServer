// memberdiff: enumerate every TypeRef/MemberRef that a plugin DLL binds into the
// game's assemblies (MD_ASSEMBLIES, comma list; default: How to Fish's set) and
// resolve each against one or more game builds. Used by tools/ci/check-game-build.sh.
//
// Original note: enumerate every TypeRef/MemberRef that a plugin DLL binds into the
// given game assemblies, and check that each resolves (name + exact signature)
// in a set of candidate game-assembly directories. Also checks a list of
// string-named reflection targets (type:kind:name[:paramcount]).
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

if (args.Length < 3) { Console.Error.WriteLine("usage: memberdiff <plugin.dll> <reflection-list.txt> <label=managedDir> [label=managedDir ...]"); return 2; }
string pluginPath = args[0];
string reflectionList = args[1];
var targets = args.Skip(2).Select(a => { var i = a.IndexOf('='); return (label: a[..i], dir: a[(i + 1)..]); }).ToList();
string[] gameAssemblies = (Environment.GetEnvironmentVariable("MD_ASSEMBLIES") ?? "Assembly-CSharp,Assembly-CSharp-firstpass,FishNet.Runtime,FishyUnityTransport,com.rlabrecque.steamworks.net").Split(',');

var refs = new List<(string asm, string type, string kind, string name, string sig)>();
using (var fs = File.OpenRead(pluginPath))
using (var pe = new PEReader(fs))
{
    var md = pe.GetMetadataReader();
    var provider = new SigProvider(md);
    string TypeRefName(TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        string name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefName((TypeReferenceHandle)tr.ResolutionScope) + "/" + name;
        string ns = md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
    string TypeRefAsm(TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeRefAsm((TypeReferenceHandle)tr.ResolutionScope);
        if (tr.ResolutionScope.Kind == HandleKind.AssemblyReference) return md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
        return "?";
    }
    foreach (var h in md.TypeReferences)
    {
        string asm = TypeRefAsm(h);
        if (gameAssemblies.Contains(asm)) refs.Add((asm, TypeRefName(h), "type", "", ""));
    }
    foreach (var h in md.MemberReferences)
    {
        var mr = md.GetMemberReference(h);
        string parentType; string asm;
        if (mr.Parent.Kind == HandleKind.TypeReference) { parentType = TypeRefName((TypeReferenceHandle)mr.Parent); asm = TypeRefAsm((TypeReferenceHandle)mr.Parent); }
        else if (mr.Parent.Kind == HandleKind.TypeSpecification)
        {
            var ts = md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
            provider.BeginCapture();
            string s = ts.DecodeSignature(provider, null);
            parentType = s; asm = provider.FirstAsm ?? "?";
        }
        else continue;
        if (!gameAssemblies.Contains(asm)) continue;
        string name = md.GetString(mr.Name);
        if (mr.GetKind() == MemberReferenceKind.Field)
        {
            string ft = mr.DecodeFieldSignature(provider, null);
            refs.Add((asm, parentType, "field", name, ft));
        }
        else
        {
            var ms = mr.DecodeMethodSignature(provider, null);
            string sig = (ms.Header.IsInstance ? "" : "static ") + ms.ReturnType + " (" + string.Join(", ", ms.ParameterTypes) + ")";
            refs.Add((asm, parentType, "method", name, sig));
        }
    }
}
refs = refs.Distinct().OrderBy(r => r.type).ThenBy(r => r.kind).ThenBy(r => r.name).ToList();

// string-named reflection targets
var reflect = File.ReadAllLines(reflectionList).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(':')).ToList();

var sb = new StringBuilder();
int missingTotal = 0;
foreach (var t in targets)
{
    var index = new GameIndex(t.dir, gameAssemblies);
    int ok = 0, missing = 0;
    var missingLines = new List<string>();
    foreach (var r in refs)
    {
        bool found = r.kind switch
        {
            "type" => index.HasType(r.type),
            "field" => index.HasField(r.type, r.name, r.sig, out _),
            "method" => index.HasMethod(r.type, r.name, r.sig, out _),
            _ => false
        };
        if (found) ok++;
        else
        {
            missing++;
            string alt = r.kind == "field" ? index.DescribeFieldAlternatives(r.type, r.name) : r.kind == "method" ? index.DescribeMethodAlternatives(r.type, r.name) : index.DescribeTypeAlternatives(r.type);
            missingLines.Add($"  MISSING {r.kind} {r.type}::{r.name} {r.sig}{alt}");
        }
    }
    int rok = 0, rmiss = 0; var rlines = new List<string>();
    foreach (var parts in reflect)
    {
        string type = parts[0], kind = parts[1], name = parts[2];
        int? pc = parts.Length > 3 && parts[3].Length > 0 ? int.Parse(parts[3]) : null;
        bool found = kind == "field" ? index.HasFieldNamed(type, name) : index.HasMethodNamed(type, name, pc);
        if (found) rok++; else { rmiss++; rlines.Add($"  MISSING reflect {kind} {type}::{name}{(pc.HasValue ? "/" + pc : "")}{(kind == "field" ? index.DescribeFieldAlternatives(type, name) : index.DescribeMethodAlternatives(type, name))}"); }
    }
    sb.AppendLine($"[{t.label}] {t.dir}");
    sb.AppendLine($"  static refs: {ok} resolved, {missing} missing (of {refs.Count})");
    foreach (var l in missingLines) sb.AppendLine(l);
    sb.AppendLine($"  reflection targets: {rok} resolved, {rmiss} missing (of {reflect.Count})");
    foreach (var l in rlines) sb.AppendLine(l);
    missingTotal += missing + rmiss;
}
Console.Write(sb.ToString());
return missingTotal == 0 ? 0 : 1;

sealed class GameIndex
{
    readonly Dictionary<string, (MetadataReader md, TypeDefinition td)> types = new(StringComparer.Ordinal);
    readonly List<PEReader> readers = new();
    public GameIndex(string dir, string[] names)
    {
        foreach (var n in names)
        {
            string p = Path.Combine(dir, n + ".dll");
            if (!File.Exists(p)) continue;
            var pe = new PEReader(File.OpenRead(p)); readers.Add(pe);
            var md = pe.GetMetadataReader();
            foreach (var h in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(h);
                types[FullName(md, td)] = (md, td);
            }
        }
    }
    static string FullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return FullName(md, md.GetTypeDefinition(decl)) + "/" + name;
        string ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
    static string Strip(string t) { int i = t.IndexOf('<'); return i < 0 ? t : t[..i]; }
    public bool HasType(string t) => types.ContainsKey(Strip(t));
    public bool HasField(string t, string name, string sig, out string actual)
    {
        actual = "";
        if (!types.TryGetValue(Strip(t), out var e)) return false;
        var prov = new SigProvider(e.md);
        foreach (var fh in e.td.GetFields())
        {
            var f = e.md.GetFieldDefinition(fh);
            if (e.md.GetString(f.Name) != name) continue;
            actual = f.DecodeSignature(prov, null);
            if (actual == sig) return true;
        }
        return false;
    }
    public bool HasMethod(string t, string name, string sig, out string actual)
    {
        actual = "";
        if (!types.TryGetValue(Strip(t), out var e)) return false;
        var prov = new SigProvider(e.md);
        foreach (var mh in e.td.GetMethods())
        {
            var m = e.md.GetMethodDefinition(mh);
            if (e.md.GetString(m.Name) != name) continue;
            var ms = m.DecodeSignature(prov, null);
            actual = (ms.Header.IsInstance ? "" : "static ") + ms.ReturnType + " (" + string.Join(", ", ms.ParameterTypes) + ")";
            if (actual == sig) return true;
        }
        return false;
    }
    public bool HasFieldNamed(string t, string name)
    {
        if (!types.TryGetValue(Strip(t), out var e)) return false;
        foreach (var fh in e.td.GetFields()) if (e.md.GetString(e.md.GetFieldDefinition(fh).Name) == name) return true;
        return false;
    }
    public bool HasMethodNamed(string t, string name, int? paramCount)
    {
        if (!types.TryGetValue(Strip(t), out var e)) return false;
        var prov = new SigProvider(e.md);
        foreach (var mh in e.td.GetMethods())
        {
            var m = e.md.GetMethodDefinition(mh);
            if (e.md.GetString(m.Name) != name) continue;
            if (!paramCount.HasValue) return true;
            if (m.DecodeSignature(prov, null).ParameterTypes.Length == paramCount.Value) return true;
        }
        return false;
    }
    public string DescribeFieldAlternatives(string t, string name)
    {
        if (!types.TryGetValue(Strip(t), out var e)) return " (type missing)";
        var prov = new SigProvider(e.md); var alts = new List<string>();
        foreach (var fh in e.td.GetFields()) { var f = e.md.GetFieldDefinition(fh); string n = e.md.GetString(f.Name); if (n == name || n.Contains(name.TrimStart('m', '_'), StringComparison.OrdinalIgnoreCase)) alts.Add(n + " : " + f.DecodeSignature(prov, null)); }
        return alts.Count == 0 ? " (no field of that name; nothing similar)" : "  => now: " + string.Join(" | ", alts);
    }
    public string DescribeMethodAlternatives(string t, string name)
    {
        if (!types.TryGetValue(Strip(t), out var e)) return " (type missing)";
        var prov = new SigProvider(e.md); var alts = new List<string>();
        foreach (var mh in e.td.GetMethods()) { var m = e.md.GetMethodDefinition(mh); string n = e.md.GetString(m.Name); if (n == name || n.Contains(name, StringComparison.OrdinalIgnoreCase)) { var ms = m.DecodeSignature(prov, null); alts.Add(n + " : " + (ms.Header.IsInstance ? "" : "static ") + ms.ReturnType + " (" + string.Join(", ", ms.ParameterTypes) + ")"); } }
        return alts.Count == 0 ? " (no method of that name; nothing similar)" : "  => now: " + string.Join(" | ", alts);
    }
    public string DescribeTypeAlternatives(string t)
    {
        string s = Strip(t); string leaf = s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] : s;
        var alts = types.Keys.Where(k => k.EndsWith(leaf, StringComparison.OrdinalIgnoreCase) || k.Contains(leaf, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
        return alts.Count == 0 ? " (no similar type)" : "  => similar: " + string.Join(" | ", alts);
    }
}

sealed class SigProvider : ISignatureTypeProvider<string, object?>
{
    readonly MetadataReader md; string? firstAsm; bool capturing;
    public void BeginCapture() { firstAsm = null; capturing = true; }
    public string? FirstAsm => firstAsm;
    public SigProvider(MetadataReader md) { this.md = md; }
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLowerInvariant();
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw) { if (capturing && firstAsm == null) firstAsm = "(self)"; var td = r.GetTypeDefinition(h); string n = r.GetString(td.Name); var d = td.GetDeclaringType(); if (!d.IsNil) return GetTypeFromDefinition(r, d, 0) + "/" + n; string ns = r.GetString(td.Namespace); return ns.Length == 0 ? n : ns + "." + n; }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        var tr = r.GetTypeReference(h); string n = r.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return GetTypeFromReference(r, (TypeReferenceHandle)tr.ResolutionScope, 0) + "/" + n;
        if (tr.ResolutionScope.Kind == HandleKind.AssemblyReference) { string a = r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name); if (capturing && firstAsm == null) firstAsm = a; }
        string ns = r.GetString(tr.Namespace); return ns.Length == 0 ? n : ns + "." + n;
    }
    public string GetTypeFromSpecification(MetadataReader r, object? g, TypeSpecificationHandle h, byte raw) => r.GetTypeSpecification(h).DecodeSignature(this, g);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(", ", a) + ">";
    public string GetGenericMethodParameter(object? g, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? g, int i) => "!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
}
