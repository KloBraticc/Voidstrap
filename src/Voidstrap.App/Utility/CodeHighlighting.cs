using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Voidstrap.Utility
{
    public static class CodeHighlighting
    {
        private const string LOG_IDENT = "CodeHighlighting";

        private const string Comment = "#7A7E85";
        private const string Str = "#6AAB73";
        private const string Number = "#2AACB8";
        private const string Keyword = "#CF8E6D";
        private const string ControlKeyword = "#CF8E6D";
        private const string Method = "#56A8F5";
        private const string TypeName = "#BCBEC4";
        private const string Variable = "#C77DBA";
        private const string Punctuation = "#808080";
        private const string Meta = "#B3AE60";
        private const string Heading = "#CF8E6D";
        private const string Added = "#6A9955";
        private const string Removed = "#F14C4C";

        private static readonly Dictionary<string, string> LegacyColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Black"] = "#D4D4D4",
            ["Navy"] = Keyword,
            ["Blue"] = Keyword,
            ["DarkBlue"] = Keyword,
            ["MidnightBlue"] = Keyword,
            ["Maroon"] = Str,
            ["DarkRed"] = Str,
            ["Brown"] = Str,
            ["Firebrick"] = Removed,
            ["Red"] = Removed,
            ["Green"] = Comment,
            ["DarkGreen"] = Comment,
            ["Olive"] = Meta,
            ["Teal"] = TypeName,
            ["DarkCyan"] = TypeName,
            ["Purple"] = ControlKeyword,
            ["DarkViolet"] = ControlKeyword,
            ["Magenta"] = ControlKeyword,
            ["Gray"] = Punctuation,
            ["DarkGray"] = Punctuation,
            ["DimGray"] = Punctuation
        };

        private static readonly (string Match, string Color)[] NameRules =
        {
            ("comment", Comment),
            ("docdefinition", Meta),
            ("doctype", Meta),
            ("declaration", Meta),
            ("cdata", Meta),
            ("entity", Meta),
            ("directive", ControlKeyword),
            ("preprocessor", ControlKeyword),
            ("regionkeyword", ControlKeyword),
            ("gotokeyword", ControlKeyword),
            ("exceptionkeyword", ControlKeyword),
            ("checkedkeyword", ControlKeyword),
            ("unsafekeyword", ControlKeyword),
            ("loopkeyword", ControlKeyword),
            ("jumpkeyword", ControlKeyword),
            ("branchkeyword", ControlKeyword),
            ("attributename", Variable),
            ("attributevalue", Str),
            ("propertyname", Variable),
            ("parametername", Variable),
            ("variable", Variable),
            ("number", Number),
            ("digit", Number),
            ("hex", Number),
            ("methodcall", Method),
            ("methodname", Method),
            ("method", Method),
            ("function", Method),
            ("string", Str),
            ("char", Str),
            ("verbatim", Str),
            ("interpolation", Str),
            ("regex", Str),
            ("namespacekeyword", Keyword),
            ("truefalse", Keyword),
            ("valuetype", TypeName),
            ("referencetype", TypeName),
            ("type", TypeName),
            ("class", TypeName),
            ("struct", TypeName),
            ("interface", TypeName),
            ("enum", TypeName),
            ("tagname", Keyword),
            ("elementname", Keyword),
            ("tag", Keyword),
            ("element", Keyword),
            ("selector", Keyword),
            ("keyword", Keyword),
            ("modifier", Keyword),
            ("operator", Punctuation),
            ("punctuation", Punctuation),
            ("bracket", Punctuation),
            ("brace", Punctuation),
            ("heading", Heading),
            ("bold", Heading),
            ("italic", Heading),
            ("link", Variable),
            ("image", Variable),
            ("added", Added),
            ("removed", Removed),
            ("deleted", Removed),
            ("error", Removed),
            ("position", Meta),
            ("filename", Meta)
        };

        private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [".xaml"] = "XML",
            [".xml"] = "XML",
            [".xshd"] = "XML",
            [".config"] = "XML",
            [".csproj"] = "XML",
            [".svg"] = "XML",
            [".json"] = "Json",
            [".jsonc"] = "Json",
            [".cs"] = "C#",
            [".rs"] = "C#",
            [".cpp"] = "C++",
            [".h"] = "C++",
            [".hpp"] = "C++",
            [".java"] = "Java",
            [".js"] = "JavaScript",
            [".mjs"] = "JavaScript",
            [".ts"] = "JavaScript",
            [".jsx"] = "JavaScript",
            [".tsx"] = "JavaScript",
            [".html"] = "HTML",
            [".htm"] = "HTML",
            [".css"] = "CSS",
            [".scss"] = "CSS",
            [".php"] = "PHP",
            [".py"] = "Python",
            [".ps1"] = "PowerShell",
            [".psm1"] = "PowerShell",
            [".sql"] = "TSQL",
            [".vb"] = "VB",
            [".md"] = "MarkDown",
            [".markdown"] = "MarkDown",
            [".patch"] = "Patch",
            [".diff"] = "Patch",
            [".tex"] = "TeX",
            [".boo"] = "Boo",
            [".lua"] = "Lua",
            [".luau"] = "Lua",
            [".rbxl"] = "Lua",
            [".rbxlx"] = "XML",
            [".axaml"] = "XML",
            [".resx"] = "XML",
            [".props"] = "XML",
            [".targets"] = "XML",
            [".manifest"] = "XML",
            [".plist"] = "XML",
            [".rss"] = "XML",
            [".atom"] = "XML",
            [".xsd"] = "XML",
            [".xsl"] = "XML",
            [".xslt"] = "XML",
            [".vbproj"] = "XML",
            [".nuspec"] = "XML",
            [".json5"] = "Json",
            [".jsonl"] = "Json",
            [".webmanifest"] = "Json",
            [".c"] = "C++",
            [".cc"] = "C++",
            [".cxx"] = "C++",
            [".hxx"] = "C++",
            [".hh"] = "C++",
            [".ino"] = "C++",
            [".cjs"] = "JavaScript",
            [".mts"] = "JavaScript",
            [".cts"] = "JavaScript",
            [".es6"] = "JavaScript",
            [".xhtml"] = "HTML",
            [".vue"] = "HTML",
            [".svelte"] = "HTML",
            [".hbs"] = "HTML",
            [".ejs"] = "HTML",
            [".asp"] = "ASP/XHTML",
            [".ascx"] = "ASP/XHTML",
            [".cshtml"] = "ASP/XHTML",
            [".razor"] = "ASP/XHTML",
            [".less"] = "CSS",
            [".sass"] = "CSS",
            [".styl"] = "CSS",
            [".php3"] = "PHP",
            [".php4"] = "PHP",
            [".php5"] = "PHP",
            [".phtml"] = "PHP",
            [".pyw"] = "Python",
            [".pyi"] = "Python",
            [".psd1"] = "PowerShell",
            [".ps1xml"] = "PowerShell",
            [".psc1"] = "PowerShell",
            [".vbs"] = "VB",
            [".bas"] = "VB",
            [".mdx"] = "MarkDown",
            [".mkd"] = "MarkDown",
            [".mdown"] = "MarkDown",
            [".mysql"] = "TSQL",
            [".pgsql"] = "TSQL",
            [".ddl"] = "TSQL",
            [".jav"] = "Java",
            [".csx"] = "C#",
            [".cake"] = "C#",
            [".latex"] = "TeX",
            [".sty"] = "TeX",
            [".rej"] = "Patch",
            [".aspx"] = "ASP/XHTML"
        };

        private static bool _initialised;

        private static readonly object _lock = new object();

        public static void Ensure()
        {
            lock (_lock)
            {
                if (_initialised)
                    return;
                _initialised = true;

                Step(RecolourBuiltIns);
                Step(RecolourBuiltIns);
                Step(RegisterLua);
                Step(RegisterGeneric);
                Step(RegisterAliases);
            }
        }

        private static void Step(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Syntax palette step failed: " + ex.Message);
            }
        }

        public static void Apply(TextEditor editor, string fileNameOrExtension)
        {
            if (editor == null)
                return;
            Ensure();
            try
            {
                editor.SyntaxHighlighting = ForFile(fileNameOrExtension);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not apply highlighting: " + ex.Message);
            }
        }

        public static IHighlightingDefinition? ForFile(string fileNameOrExtension)
        {
            Ensure();
            if (string.IsNullOrWhiteSpace(fileNameOrExtension))
                return null;

            string ext = fileNameOrExtension.StartsWith('.')
                ? fileNameOrExtension
                : Path.GetExtension(fileNameOrExtension);

            if (string.IsNullOrEmpty(ext))
                ext = "." + fileNameOrExtension.Trim();

            if (ExtensionMap.TryGetValue(ext, out string? name))
            {
                IHighlightingDefinition? mapped = HighlightingManager.Instance.GetDefinition(name);
                if (mapped != null)
                    return mapped;
            }
            return HighlightingManager.Instance.GetDefinitionByExtension(ext)
                ?? (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : HighlightingManager.Instance.GetDefinition("Generic"));
        }

        public static IEnumerable<string> LanguageNames =>
            HighlightingManager.Instance.HighlightingDefinitions.Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        private static void RecolourBuiltIns()
        {
            System.Reflection.Assembly assembly = typeof(HighlightingManager).Assembly;

            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.EndsWith(".xshd", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    XDocument doc;
                    using (Stream? stream = assembly.GetManifestResourceStream(resource))
                    {
                        if (stream == null)
                            continue;
                        doc = XDocument.Load(stream);
                    }

                    if (doc.Root == null)
                        continue;

                    string? name = (string?)doc.Root.Attribute("name");
                    string[] extensions = ((string?)doc.Root.Attribute("extensions") ?? "")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries);

                    if (string.IsNullOrEmpty(name))
                        continue;

                    Recolour(doc);
                    Register(name, extensions, doc);
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine(LOG_IDENT, "Skipped " + resource + ": " + ex.Message);
                }
            }
        }

        private static void Recolour(XDocument doc)
        {
            foreach (XElement element in doc.Descendants())
            {
                XAttribute? foreground = element.Attribute("foreground");
                if (foreground == null)
                    continue;

                string? replacement = Classify((string?)element.Attribute("name"))
                    ?? Classify(element.Name.LocalName)
                    ?? Legacy(foreground.Value);

                if (replacement != null)
                    foreground.Value = replacement;

                element.Attribute("background")?.Remove();
            }
        }

        private static string? Classify(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            string lower = name.ToLowerInvariant();
            foreach ((string match, string color) in NameRules)
            {
                if (lower.Contains(match, StringComparison.Ordinal))
                    return color;
            }
            return null;
        }

        private static string? Legacy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (LegacyColors.TryGetValue(value.Trim(), out string? mapped))
                return mapped;
            if (!TryLuminance(value, out double luminance) || luminance > 0.45)
                return null;
            return "#D4D4D4";
        }

        private static bool TryLuminance(string value, out double luminance)
        {
            luminance = 1.0;
            try
            {
                object parsed = ColorConverter.ConvertFromString(value.Trim());
                if (parsed is not Color c)
                    return false;
                luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Register(string name, string[] extensions, XDocument doc)
        {
            using XmlReader reader = doc.CreateReader();
            XshdSyntaxDefinition definition = HighlightingLoader.LoadXshd(reader);
            HighlightingManager.Instance.RegisterHighlighting(
                name,
                extensions,
                HighlightingLoader.Load(definition, HighlightingManager.Instance));
        }

        private static void RegisterAliases()
        {
            IHighlightingDefinition? xml = HighlightingManager.Instance.GetDefinition("XML");
            if (xml != null)
                HighlightingManager.Instance.RegisterHighlighting("XAML", extensions, xml);
        }

        private static void RegisterGeneric()
        {
            HighlightingManager.Instance.RegisterHighlighting("Generic", [".generic"], LoadDocument(XDocument.Parse(GenericSyntax)));
        }

        private const string GenericSyntax = """
<SyntaxDefinition name="Generic" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#7A7E85" />
  <Color name="String" foreground="#6AAB73" />
  <Color name="Number" foreground="#2AACB8" />
  <Color name="Keyword" foreground="#CF8E6D" />
  <Color name="MethodCall" foreground="#56A8F5" />
  <RuleSet>
    <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
    <Span color="Comment" begin="//" />
    <Span color="Comment" begin="(?&lt;![\w&amp;$])#(?!\[|!)" />
    <Span color="Comment" begin="(?&lt;!\S)--(?=\s)" />
    <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
    <Span color="String" begin="'" end="'"><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
    <Span color="String" multiline="true" begin="`" end="`" />
    <Keywords color="Keyword">
      <Word>if</Word><Word>else</Word><Word>elif</Word><Word>for</Word><Word>while</Word><Word>do</Word><Word>switch</Word><Word>case</Word><Word>match</Word><Word>when</Word><Word>return</Word><Word>break</Word><Word>continue</Word>
      <Word>func</Word><Word>fn</Word><Word>fun</Word><Word>def</Word><Word>function</Word><Word>class</Word><Word>struct</Word><Word>enum</Word><Word>interface</Word><Word>trait</Word><Word>impl</Word><Word>type</Word><Word>module</Word><Word>package</Word><Word>import</Word><Word>from</Word><Word>use</Word><Word>using</Word><Word>include</Word><Word>namespace</Word>
      <Word>let</Word><Word>var</Word><Word>val</Word><Word>const</Word><Word>mut</Word><Word>static</Word><Word>public</Word><Word>private</Word><Word>protected</Word><Word>async</Word><Word>await</Word><Word>try</Word><Word>catch</Word><Word>except</Word><Word>finally</Word><Word>throw</Word><Word>raise</Word>
      <Word>true</Word><Word>false</Word><Word>null</Word><Word>nil</Word><Word>none</Word><Word>end</Word><Word>then</Word><Word>in</Word><Word>new</Word><Word>self</Word><Word>this</Word>
    </Keywords>
    <Rule color="MethodCall">\b[A-Za-z_][\w]*(?=\s*\()</Rule>
    <Rule color="Number">\b0[xX][0-9a-fA-F]+|\b\d+(\.\d+)?([eE][+-]?\d+)?</Rule>
  </RuleSet>
</SyntaxDefinition>
""";

        private static void RegisterLua()
        {
            XDocument doc = XDocument.Parse(LuaSyntax);
            HighlightingManager.Instance.RegisterHighlighting("Lua", extensionsArray, LoadDocument(doc));
        }

        private static IHighlightingDefinition LoadDocument(XDocument doc)
        {
            using XmlReader reader = doc.CreateReader();
            return HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
        }

        private const string LuaSyntax = """
<SyntaxDefinition name="Lua" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#7A7E85" />
  <Color name="String" foreground="#6AAB73" />
  <Color name="Number" foreground="#2AACB8" />
  <Color name="Keyword" foreground="#CF8E6D" />
  <Color name="ControlKeyword" foreground="#CF8E6D" />
  <Color name="GlobalType" foreground="#4EC9B0" />
  <Color name="MethodCall" foreground="#56A8F5" />
  <RuleSet ignoreCase="false">
    <Span color="Comment" multiline="true" begin="--\[\[" end="\]\]" />
    <Span color="Comment" begin="--" />
    <Span color="String" multiline="true" begin="\[\[" end="\]\]" />
    <Span color="String">
      <Begin>"</Begin>
      <End>"</End>
      <RuleSet>
        <Span begin="\\" end="." />
      </RuleSet>
    </Span>
    <Span color="String">
      <Begin>'</Begin>
      <End>'</End>
      <RuleSet>
        <Span begin="\\" end="." />
      </RuleSet>
    </Span>
    <Keywords color="ControlKeyword">
      <Word>if</Word>
      <Word>then</Word>
      <Word>else</Word>
      <Word>elseif</Word>
      <Word>end</Word>
      <Word>for</Word>
      <Word>while</Word>
      <Word>repeat</Word>
      <Word>until</Word>
      <Word>do</Word>
      <Word>break</Word>
      <Word>continue</Word>
      <Word>return</Word>
      <Word>goto</Word>
    </Keywords>
    <Keywords color="Keyword">
      <Word>local</Word>
      <Word>function</Word>
      <Word>and</Word>
      <Word>or</Word>
      <Word>not</Word>
      <Word>in</Word>
      <Word>nil</Word>
      <Word>true</Word>
      <Word>false</Word>
      <Word>self</Word>
      <Word>export</Word>
      <Word>type</Word>
    </Keywords>
    <Keywords color="GlobalType">
      <Word>game</Word>
      <Word>workspace</Word>
      <Word>script</Word>
      <Word>math</Word>
      <Word>string</Word>
      <Word>table</Word>
      <Word>task</Word>
      <Word>os</Word>
      <Word>coroutine</Word>
      <Word>Instance</Word>
      <Word>Vector3</Word>
      <Word>Vector2</Word>
      <Word>CFrame</Word>
      <Word>Color3</Word>
      <Word>UDim2</Word>
      <Word>Enum</Word>
    </Keywords>
    <Rule color="MethodCall">[\w]+(?=\s*\()</Rule>
    <Rule color="Number">\b0[xX][0-9a-fA-F]+|(\b\d+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?</Rule>
  </RuleSet>
</SyntaxDefinition>
""";
        private static readonly string[] extensions = new[] { ".xaml" };
        private static readonly string[] extensionsArray = new[] { ".lua", ".luau" };
    }
}
