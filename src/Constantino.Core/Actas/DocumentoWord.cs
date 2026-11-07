using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Constantino.Core.Actas;

/// <summary>
/// Convierte el Markdown sencillo de un acta (títulos, viñetas, negritas y cursivas, tablas) en un documento de Word
/// (.docx) sin librerías: un .docx es un zip con unos pocos XML.
/// </summary>
public static partial class DocumentoWord
{
    public static void Guardar(string markdown, string ruta)
    {
        var temporal = ruta + ".tmp";
        using (var zip = ZipFile.Open(temporal, ZipArchiveMode.Create))
        {
            Escribir(zip, "[Content_Types].xml", TiposDeContenido);
            Escribir(zip, "_rels/.rels", Relaciones);
            Escribir(zip, "word/_rels/document.xml.rels", RelacionesDocumento);
            Escribir(zip, "word/styles.xml", Estilos);
            Escribir(zip, "word/document.xml", Documento(markdown));
        }
        File.Move(temporal, ruta, overwrite: true);
    }

    private static void Escribir(ZipArchive zip, string nombre, string contenido)
    {
        using var w = new StreamWriter(zip.CreateEntry(nombre, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(contenido);
    }

    /// <summary>El cuerpo del documento (word/document.xml).</summary>
    public static string Documento(string markdown)
    {
        var cuerpo = new StringBuilder();
        var lineas = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lineas.Length; i++)
        {
            var linea = lineas[i].TrimEnd();
            var limpia = linea.TrimStart();
            if (limpia.Length == 0 || Regex.IsMatch(limpia, @"^(-{3,}|\*{3,}|_{3,})$"))
                continue;
            if (limpia.StartsWith('|'))
            {
                var filas = new List<string>();
                for (; i < lineas.Length && lineas[i].TrimStart().StartsWith('|'); i++)
                    filas.Add(lineas[i].Trim());
                i--;
                cuerpo.Append(Tabla(filas));
                continue;
            }
            if (Titulo().Match(limpia) is { Success: true } t)
            {
                cuerpo.Append(Parrafo(t.Groups[2].Value, $"Titulo{Math.Min(t.Groups[1].Value.Length, 3)}"));
                continue;
            }
            if (Vineta().Match(linea) is { Success: true } v)
            {
                var nivel = Math.Min(v.Groups[1].Value.Replace("\t", "  ").Length / 2, 3);
                cuerpo.Append(Parrafo("•\t" + v.Groups[2].Value, "Vineta", sangria: 360 * (nivel + 1)));
                continue;
            }
            if (Numerada().Match(linea) is { Success: true } n)
            {
                cuerpo.Append(Parrafo(n.Groups[2].Value + "\t" + n.Groups[3].Value, "Vineta", sangria: 360));
                continue;
            }
            cuerpo.Append(Parrafo(limpia, null));
        }
        return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
               """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""" +
               cuerpo +
               """<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1417" w:right="1417" w:bottom="1417" w:left="1417" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr>""" +
               "</w:body></w:document>";
    }

    private static string Parrafo(string texto, string? estilo, int sangria = 0)
    {
        var propiedades = new StringBuilder();
        if (estilo is not null)
            propiedades.Append($"""<w:pStyle w:val="{estilo}"/>""");
        if (sangria > 0)
            propiedades.Append($"""<w:tabs><w:tab w:val="left" w:pos="{sangria}"/></w:tabs><w:ind w:left="{sangria}" w:hanging="360"/>""");
        return $"<w:p>{(propiedades.Length > 0 ? $"<w:pPr>{propiedades}</w:pPr>" : "")}{Tramos(texto)}</w:p>";
    }

    private static string Tabla(List<string> filas)
    {
        // La fila de separación («|---|---|») no se muestra; la primera fila es la cabecera.
        var celdas = filas.Where(f => !Regex.IsMatch(f, @"^\|[\s:\-|]+\|?$"))
            .Select(f => f.Trim('|').Split('|').Select(c => c.Trim()).ToList())
            .ToList();
        if (celdas.Count == 0)
            return "";
        var columnas = celdas.Max(c => c.Count);
        var sb = new StringBuilder("""<w:tbl><w:tblPr><w:tblStyle w:val="Tabla"/><w:tblW w:w="5000" w:type="pct"/><w:tblBorders>""");
        foreach (var borde in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            sb.Append($"""<w:{borde} w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/>""");
        sb.Append("</w:tblBorders></w:tblPr>");
        for (var f = 0; f < celdas.Count; f++)
        {
            sb.Append("<w:tr>");
            for (var c = 0; c < columnas; c++)
            {
                var texto = c < celdas[f].Count ? celdas[f][c] : "";
                var sombra = f == 0 ? """<w:tcPr><w:shd w:val="clear" w:color="auto" w:fill="E7E6E6"/></w:tcPr>""" : "<w:tcPr/>";
                sb.Append($"<w:tc>{sombra}<w:p>{Tramos(f == 0 ? $"**{texto.Trim('*')}**" : texto)}</w:p></w:tc>");
            }
            sb.Append("</w:tr>");
        }
        return sb.Append("</w:tbl>").ToString();
    }

    /// <summary>El texto con sus negritas (**…**) y cursivas (*…*) como tramos de Word.</summary>
    private static string Tramos(string texto)
    {
        var sb = new StringBuilder();
        foreach (Match m in Formato().Matches(texto))
        {
            var (contenido, negrita, cursiva) = m.Groups[1].Success ? (m.Groups[1].Value, true, false)
                : m.Groups[2].Success ? (m.Groups[2].Value, false, true)
                : (m.Groups[3].Value, false, false);
            if (contenido.Length == 0)
                continue;
            var propiedades = (negrita ? "<w:b/>" : "") + (cursiva ? "<w:i/>" : "");
            foreach (var (trozo, k) in contenido.Split('\t').Select((x, k) => (x, k)))
            {
                if (k > 0)
                    sb.Append("<w:r><w:tab/></w:r>");
                if (trozo.Length > 0)
                    sb.Append($"""<w:r>{(propiedades.Length > 0 ? $"<w:rPr>{propiedades}</w:rPr>" : "")}<w:t xml:space="preserve">{SecurityElement.Escape(trozo)}</w:t></w:r>""");
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Titulo();

    [GeneratedRegex(@"^(\s*)[-*•]\s+(.*)$")]
    private static partial Regex Vineta();

    [GeneratedRegex(@"^(\s*)(\d+[.)])\s+(.*)$")]
    private static partial Regex Numerada();

    [GeneratedRegex(@"\*\*(.+?)\*\*|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|([^*]+|\*)")]
    private static partial Regex Formato();

    private const string TiposDeContenido = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>""";

    private const string Relaciones = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""";

    private const string RelacionesDocumento = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""";

    private const string Estilos = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:lang w:val="es-ES"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="264" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style><w:style w:type="paragraph" w:styleId="Titulo1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:color w:val="1F3864"/><w:sz w:val="32"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="Titulo2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:color w:val="2F5496"/><w:sz w:val="26"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="Titulo3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="160" w:after="60"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:sz w:val="24"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="Vineta"><w:name w:val="List Bullet"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="60"/></w:pPr></w:style><w:style w:type="table" w:styleId="Tabla"><w:name w:val="Table Grid"/><w:tblPr><w:tblCellMar><w:left w:w="108" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style></w:styles>""";
}
