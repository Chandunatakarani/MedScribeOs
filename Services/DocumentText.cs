using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MedScribeOS.Services;

/// <summary>Plain-text extraction from the document types the File Analyzer accepts.</summary>
public static class DocumentText
{
    /// <summary>Reads text from .txt / .pdf / .docx. Throws for anything else.</summary>
    public static string FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => File.ReadAllText(path),
            ".pdf" => FromPdf(path),
            ".docx" => FromDocx(path),
            _ => throw new NotSupportedException($"Can't read text from a '{ext}' file."),
        };
    }

    public static bool IsDocument(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".pdf" or ".docx";

    public static bool IsAudio(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".mp3" or ".m4a";

    private static string FromPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString().Trim();
    }

    private static string FromDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var doc = zip.GetEntry("word/document.xml")
                  ?? throw new InvalidOperationException("That doesn't look like a valid .docx (no word/document.xml).");

        using var reader = new StreamReader(doc.Open());
        var xml = reader.ReadToEnd();

        // Paragraph and line breaks -> newlines, then drop every remaining tag.
        xml = Regex.Replace(xml, "</w:p>", "\n");
        xml = Regex.Replace(xml, "<w:br[^>]*/>", "\n");
        xml = Regex.Replace(xml, "<[^>]+>", "");
        return WebUtility.HtmlDecode(xml).Trim();
    }
}
