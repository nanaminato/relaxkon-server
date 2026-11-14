using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace RelaxKon.Kit.Word;

public class WordDocumentProcessor: IWordDocumentProcessor
{
    private static readonly XNamespace Wordprocessing = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public string Process(string localPath)
    {
        var builder = new StringBuilder();
        using var archive = ZipFile.OpenRead(localPath);
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The DOCX file does not contain word/document.xml.");
        using var documentStream = documentEntry.Open();
        using var reader = System.Xml.XmlReader.Create(documentStream, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            MaxCharactersInDocument = 16 * 1024 * 1024
        });
        var document = XDocument.Load(reader);

        foreach (var paragraph in document.Descendants(Wordprocessing + "p"))
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == Wordprocessing + "t")
                {
                    builder.Append(node.Value);
                }
                else if (node.Name == Wordprocessing + "tab")
                {
                    builder.Append('\t');
                }
            }

            if (paragraph.Descendants(Wordprocessing + "t").Any())
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }
}
