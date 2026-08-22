using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DriftwoodHost
{
	// A tiny hand-rolled writer. The game ships Newtonsoft, but binding the host mod to the
	// game's copy of a third-party library is a build-churn liability for a file this simple,
	// and the supervisor has to be able to parse it with System.Text.Json on the other side.
	internal sealed class Json
	{
		private readonly StringBuilder _builder = new StringBuilder();
		private bool _first = true;

		public static Json Object() => new Json().Open();

		private Json Open()
		{
			_builder.Append('{');
			return this;
		}

		public Json Add(string name, string value)
		{
			Separator();
			Key(name);
			if (value == null) _builder.Append("null");
			else Escape(value);
			return this;
		}

		public Json Add(string name, bool value)
		{
			Separator();
			Key(name);
			_builder.Append(value ? "true" : "false");
			return this;
		}

		public Json Add(string name, int value)
		{
			Separator();
			Key(name);
			_builder.Append(value.ToString(CultureInfo.InvariantCulture));
			return this;
		}

		public Json Add(string name, long value)
		{
			Separator();
			Key(name);
			_builder.Append(value.ToString(CultureInfo.InvariantCulture));
			return this;
		}

		public Json Add(string name, double value)
		{
			Separator();
			Key(name);
			_builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
			return this;
		}

		public Json AddStrings(string name, IEnumerable<string> values)
		{
			Separator();
			Key(name);
			_builder.Append('[');
			bool first = true;
			foreach (string value in values)
			{
				if (!first) _builder.Append(',');
				first = false;
				Escape(value);
			}
			_builder.Append(']');
			return this;
		}

		public Json AddRaw(string name, string rawJson)
		{
			Separator();
			Key(name);
			_builder.Append(rawJson);
			return this;
		}

		public string Close()
		{
			_builder.Append('}');
			return _builder.ToString();
		}

		private void Separator()
		{
			if (!_first) _builder.Append(',');
			_first = false;
		}

		private void Key(string name)
		{
			Escape(name);
			_builder.Append(':');
		}

		private void Escape(string value)
		{
			_builder.Append('"');
			foreach (char c in value ?? string.Empty)
			{
				switch (c)
				{
					case '"': _builder.Append("\\\""); break;
					case '\\': _builder.Append("\\\\"); break;
					case '\n': _builder.Append("\\n"); break;
					case '\r': _builder.Append("\\r"); break;
					case '\t': _builder.Append("\\t"); break;
					default:
						if (c < ' ') _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						else _builder.Append(c);
						break;
				}
			}
			_builder.Append('"');
		}
	}
}
