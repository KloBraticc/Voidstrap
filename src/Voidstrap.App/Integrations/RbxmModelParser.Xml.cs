using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

public static partial class RbxmModelParser
{
    private const int MaxXmlBytes = 32 * 1024 * 1024;

    private static readonly byte[] XmlMagic = "<roblox "u8.ToArray();

    private static bool IsXmlModel(byte[] data)
    {
        int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        if (data.Length < start + XmlMagic.Length)
            return false;
        for (int i = 0; i < XmlMagic.Length; i++)
            if (data[start + i] != XmlMagic[i])
                return false;
        return true;
    }

    private static List<RbxmInstance> ReadXmlTree(byte[] data)
    {
        List<RbxmInstance> roots = new();
        if (data.Length > MaxXmlBytes)
            return roots;
        try
        {
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes };
            using MemoryStream stream = new(data);
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument document = XDocument.Load(reader);
            if (document.Root == null)
                return roots;
            int total = 0;
            foreach (XElement item in document.Root.Elements("Item"))
                if (ReadXmlItem(item, null, ref total) is RbxmInstance instance)
                    roots.Add(instance);
        }
        catch (XmlException)
        {
            roots.Clear();
        }
        return roots;
    }

    private static RbxmInstance? ReadXmlItem(XElement item, RbxmInstance? parent, ref int total)
    {
        if (++total > MaxTotalInstances)
            return null;
        RbxmInstance instance = new() { ClassName = item.Attribute("class")?.Value ?? "", Parent = parent };
        if (item.Element("Properties") is XElement properties)
            foreach (XElement property in properties.Elements())
            {
                string? name = property.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name))
                    continue;
                switch (property.Name.LocalName)
                {
                    case "string":
                        if (name == "Name")
                            instance.Name = property.Value;
                        else
                            instance.Strings[name] = property.Value;
                        break;
                    case "Content":
                    case "ContentId":
                        instance.Strings[name] = (property.Element("url") ?? property.Element("uri"))?.Value ?? "";
                        break;
                    case "Vector3":
                        instance.Vectors[name] = new Vector3D(XmlNumber(property, "X"), XmlNumber(property, "Y"), XmlNumber(property, "Z"));
                        break;
                    case "CoordinateFrame":
                        instance.CFrames[name] = new RobloxCFrame(
                            XmlNumber(property, "X"),
                            XmlNumber(property, "Y"),
                            XmlNumber(property, "Z"),
                            XmlNumber(property, "R00", 1),
                            XmlNumber(property, "R01"),
                            XmlNumber(property, "R02"),
                            XmlNumber(property, "R10"),
                            XmlNumber(property, "R11", 1),
                            XmlNumber(property, "R12"),
                            XmlNumber(property, "R20"),
                            XmlNumber(property, "R21"),
                            XmlNumber(property, "R22", 1)
                        );
                        break;
                    case "float":
                    case "double":
                    case "int":
                    case "int64":
                    case "token":
                        if (double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
                            instance.Numbers[name] = number;
                        break;
                }
            }
        foreach (XElement child in item.Elements("Item"))
            if (ReadXmlItem(child, instance, ref total) is RbxmInstance nested)
                instance.Children.Add(nested);
        return instance;
    }

    private static double XmlNumber(XElement parent, string name, double fallback = 0) =>
        parent.Element(name) is XElement element && double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : fallback;
}
