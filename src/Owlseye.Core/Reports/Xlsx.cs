// Minimal .xlsx writer (SpreadsheetML in a zip), no library: strings only, a bold header row, frozen panes, filters
// and column widths. Streams each sheet, so a large access table does not have to fit in memory as XML.

using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Owlseye.Reports;

/// <param name="Name">sheet name (at most 31 characters, no []:*?/\)</param>
/// <param name="Header">first row, bold; frozen and filterable</param>
/// <param name="Rows">the data rows</param>
/// <param name="Widths">column widths in characters (missing ones: 14)</param>
/// <param name="FreezeColumns">columns kept in view when scrolling right (the matrix keeps its folder column)</param>
/// <param name="RotateHeader">header text turned 90° (the matrix columns are account names)</param>
public sealed record Sheet(string Name, IReadOnlyList<string> Header, IEnumerable<IReadOnlyList<string>> Rows,
    IReadOnlyList<double>? Widths = null, int FreezeColumns = 0, bool RotateHeader = false);

public static class Xlsx
{
    const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false), Indent = false };

    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Part(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(w, "xml", "application/xml");
                Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for (var i = 1; i <= sheets.Count; i++)
                    Override(w, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                w.WriteEndElement();
            });
            Part(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", PkgRel);
                Relationship(w, "rId1", Rel + "/officeDocument", "xl/workbook.xml");
                w.WriteEndElement();
            });
            Part(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", Main);
                w.WriteAttributeString("xmlns", "r", null, Rel);
                w.WriteStartElement("sheets", Main);
                for (var i = 0; i < sheets.Count; i++)
                {
                    w.WriteStartElement("sheet", Main);
                    w.WriteAttributeString("name", SheetName(sheets[i].Name));
                    w.WriteAttributeString("sheetId", (i + 1).ToString());
                    w.WriteAttributeString("id", Rel, $"rId{i + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
            });
            Part(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", PkgRel);
                for (var i = 1; i <= sheets.Count; i++) Relationship(w, $"rId{i}", Rel + "/worksheet", $"worksheets/sheet{i}.xml");
                Relationship(w, $"rId{sheets.Count + 1}", Rel + "/styles", "styles.xml");
                w.WriteEndElement();
            });
            Part(zip, "xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                Part(zip, $"xl/worksheets/sheet{i + 1}.xml", w => Worksheet(w, sheet));
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    static void Part(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var w = XmlWriter.Create(s, Settings);
        w.WriteStartDocument(true);
        write(w);
        w.WriteEndDocument();
    }

    static void Default(XmlWriter w, string ext, string type)
    {
        w.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("Extension", ext);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    static void Override(XmlWriter w, string part, string type)
    {
        w.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("PartName", part);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    static void Relationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PkgRel);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    /// <summary>cellXfs: 0 normal, 1 bold header, 2 bold header turned 90°, 3 wrapped text.</summary>
    static void Styles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", Main);
        w.WriteStartElement("fonts", Main);
        w.WriteAttributeString("count", "2");
        Font(w, false);
        Font(w, true);
        w.WriteEndElement();
        w.WriteStartElement("fills", Main);
        w.WriteAttributeString("count", "2");
        foreach (var pattern in new[] { "none", "gray125" })
        {
            w.WriteStartElement("fill", Main);
            w.WriteStartElement("patternFill", Main);
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteStartElement("borders", Main);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", Main);
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" }) w.WriteElementString(side, Main, "");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("cellStyleXfs", Main);
        w.WriteAttributeString("count", "1");
        Xf(w, 0, false, 0, false);
        w.WriteEndElement();
        w.WriteStartElement("cellXfs", Main);
        w.WriteAttributeString("count", "4");
        Xf(w, 0, false, 0, false);
        Xf(w, 1, true, 0, false);
        Xf(w, 1, true, 90, false);
        Xf(w, 0, true, 0, true);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    static void Font(XmlWriter w, bool bold)
    {
        w.WriteStartElement("font", Main);
        if (bold) w.WriteElementString("b", Main, "");
        w.WriteStartElement("sz", Main);
        w.WriteAttributeString("val", "11");
        w.WriteEndElement();
        w.WriteStartElement("name", Main);
        w.WriteAttributeString("val", "Calibri");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    static void Xf(XmlWriter w, int font, bool apply, int rotation, bool wrap)
    {
        w.WriteStartElement("xf", Main);
        w.WriteAttributeString("numFmtId", "0");
        w.WriteAttributeString("fontId", font.ToString());
        w.WriteAttributeString("fillId", "0");
        w.WriteAttributeString("borderId", "0");
        if (apply)
        {
            w.WriteAttributeString("applyFont", "1");
            if (rotation != 0 || wrap)
            {
                w.WriteAttributeString("applyAlignment", "1");
                w.WriteStartElement("alignment", Main);
                if (rotation != 0) w.WriteAttributeString("textRotation", rotation.ToString());
                if (wrap) w.WriteAttributeString("wrapText", "1");
                w.WriteAttributeString("vertical", "top");
                w.WriteEndElement();
            }
        }
        w.WriteEndElement();
    }

    static void Worksheet(XmlWriter w, Sheet sheet)
    {
        w.WriteStartElement("worksheet", Main);
        w.WriteStartElement("sheetViews", Main);
        w.WriteStartElement("sheetView", Main);
        w.WriteAttributeString("workbookViewId", "0");
        w.WriteStartElement("pane", Main);
        if (sheet.FreezeColumns > 0) w.WriteAttributeString("xSplit", sheet.FreezeColumns.ToString());
        w.WriteAttributeString("ySplit", "1");
        w.WriteAttributeString("topLeftCell", Ref(sheet.FreezeColumns, 1));
        w.WriteAttributeString("activePane", sheet.FreezeColumns > 0 ? "bottomRight" : "bottomLeft");
        w.WriteAttributeString("state", "frozen");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("cols", Main);
        for (var c = 0; c < sheet.Header.Count; c++)
        {
            w.WriteStartElement("col", Main);
            w.WriteAttributeString("min", (c + 1).ToString());
            w.WriteAttributeString("max", (c + 1).ToString());
            var width = sheet.Widths is { } ws && c < ws.Count ? ws[c] : 14;
            w.WriteAttributeString("width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteAttributeString("customWidth", "1");
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteStartElement("sheetData", Main);
        Row(w, 0, sheet.Header, sheet.RotateHeader ? 2 : 1, sheet.RotateHeader ? 1 : 0);
        var r = 1;
        foreach (var row in sheet.Rows) Row(w, r++, row, 0, 0);
        w.WriteEndElement();
        if (sheet.Header.Count > 0)
        {
            w.WriteStartElement("autoFilter", Main);
            w.WriteAttributeString("ref", $"{Ref(0, 0)}:{Ref(sheet.Header.Count - 1, Math.Max(r - 1, 1))}");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>style: for all cells except the first `plainFirst` ones (the rotated matrix header keeps "Folder" upright).</summary>
    static void Row(XmlWriter w, int r, IReadOnlyList<string> cells, int style, int plainFirst)
    {
        w.WriteStartElement("row", Main);
        w.WriteAttributeString("r", (r + 1).ToString());
        for (var c = 0; c < cells.Count; c++)
        {
            var v = cells[c];
            if (string.IsNullOrEmpty(v)) continue;
            w.WriteStartElement("c", Main);
            w.WriteAttributeString("r", Ref(c, r));
            w.WriteAttributeString("t", "inlineStr");
            var s = style == 2 && c < plainFirst ? 1 : style;
            if (s != 0) w.WriteAttributeString("s", s.ToString());
            w.WriteStartElement("is", Main);
            w.WriteStartElement("t", Main);
            var text = Clean(v);
            if (text != text.Trim()) w.WriteAttributeString("xml", "space", null, "preserve");
            w.WriteString(text);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    /// <summary>A1 reference for zero-based column and row.</summary>
    public static string Ref(int col, int row)
    {
        var letters = "";
        for (var c = col + 1; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
        return letters + (row + 1);
    }

    static string SheetName(string name)
    {
        var clean = new string(name.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray());
        return clean.Length > 31 ? clean[..31] : clean;
    }

    /// <summary>Characters XML cannot hold (control characters, unpaired surrogates in odd folder names) become U+FFFD,
    /// cells are cut at Excel's 32,767 characters.</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[++i]);
            }
            else if (char.IsSurrogate(c) || !XmlConvert.IsXmlChar(c)) sb.Append('�');
            else sb.Append(c);
        }
        return sb.Length > 32767 ? sb.ToString(0, 32767) : sb.ToString();
    }
}
