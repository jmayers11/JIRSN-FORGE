using System.Globalization;
using System.Security;
using System.Text;
using Forge.Kernel.Commands;
using Forge.Kernel.Serialization;
using ZXing;
using ZXing.Common;

namespace Forge.Host.Identification;

public sealed record LabelArtifact(byte[] Bytes, string MediaType, string FileName, string Sha256);

/// <summary>Pure barcode rendering. No printer, stock, URL execution or physical-confirmation side effect.</summary>
public sealed class LabelRendering
{
    public LabelArtifact Render(LabelCopyView label, LabelTemplateView template)
    {
        if (template.Width is < 96 or > 2048 || template.Height is < 64 or > 2048)
            throw new CommandRejectedException("label_dimensions", "Use bounded label dimensions in pixels.");
        var format = label.Symbology switch
        {
            LabelSymbology.Code128 => BarcodeFormat.CODE_128,
            LabelSymbology.QR => BarcodeFormat.QR_CODE,
            LabelSymbology.DataMatrix => BarcodeFormat.DATA_MATRIX,
            _ => throw new CommandRejectedException("label_symbology", "This barcode symbology is not configured for rendering."),
        };
        var barcodeHeight = Math.Max(32, template.Height - 32);
        var writer = new BarcodeWriterGeneric { Format = format, Options = new EncodingOptions { Width = template.Width, Height = barcodeHeight, Margin = 4, PureBarcode = true } };
        var matrix = writer.Encode(label.Code);
        if (matrix.Width > template.Width || matrix.Height > barcodeHeight)
            throw new CommandRejectedException("label_template_too_small", "The selected template cannot contain a readable barcode. Use an approved larger template.");
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(template.Width).Append("\" height=\"").Append(template.Height)
            .Append("\" viewBox=\"0 0 ").Append(template.Width).Append(' ').Append(template.Height).Append("\"><rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
        bool SameRow(int left,int right)
        {
            for(var column=0;column<matrix.Width;column++)if(matrix[column,left]!=matrix[column,right])return false;
            return true;
        }
        for (var y = 0; y < matrix.Height; y++)
        {
            var rows=1;while(y+rows<matrix.Height&&SameRow(y,y+rows))rows++;
            for (var x = 0; x < matrix.Width; x++)
            {
                if (!matrix[x, y]) continue;
                var from = x;
                while (x + 1 < matrix.Width && matrix[x + 1, y]) x++;
                svg.Append("<rect x=\"").Append(from).Append("\" y=\"").Append(y).Append("\" width=\"").Append(x - from + 1).Append("\" height=\"").Append(rows).Append("\" fill=\"black\"/>");
                if(svg.Length>8*1024*1024)throw new CommandRejectedException("label_artifact_too_large","The label exceeds the bounded artifact size.");
            }
            y+=rows-1;
        }
        var subject=label.Subject;
        var identity=subject.Kind=="output"?(subject.IdentityKind=="UntrackedOutput"?"recorded untracked output ":"recorded output ")+subject.RecordNumber?.ToString(CultureInfo.InvariantCulture)+"/"+subject.LineNumber?.ToString(CultureInfo.InvariantCulture)
            :subject.OrderNumber is{} order?"Order "+order.ToString(CultureInfo.InvariantCulture)+(subject.Sequence is{} sequence?" op "+sequence.ToString(CultureInfo.InvariantCulture):"")
            :subject.SubjectKey ?? subject.LocationCode ?? subject.PartNumber ?? label.SubjectCode;
        var caption = subject.Kind+" "+identity+(subject.PartNumber is{} part&&part!=identity?" "+part:"")
            +(subject.LotNumber is{} lot?" lot "+lot:"")+(subject.Revision is{} revision?" rev "+revision:"")
            +(subject.Kind=="person"&&subject.DisplayName is{} name?" "+name:"");
        try{System.Xml.XmlConvert.VerifyXmlChars(caption);}catch(System.Xml.XmlException){throw new CommandRejectedException("label_caption","The recorded human-readable identity cannot be safely rendered.");}
        var runes=caption.EnumerateRunes().Select(rune=>rune.ToString()).ToArray();var perLine=Math.Max(1,(template.Width-8)/12);
        if(runes.Length>perLine*2)throw new CommandRejectedException("label_template_too_small","Use an approved label large enough for the complete human-readable identity.");
        for(var line=0;line*perLine<runes.Length;line++)
            svg.Append("<text x=\"4\" y=\"").Append(template.Height-20+line*14).Append("\" font-family=\"monospace\" font-size=\"12\">")
                .Append(SecurityElement.Escape(string.Concat(runes.Skip(line*perLine).Take(perLine)))).Append("</text>");
        svg.Append("</svg>");
        var bytes = Encoding.UTF8.GetBytes(svg.ToString());
        if (bytes.Length > 8 * 1024 * 1024) throw new CommandRejectedException("label_artifact_too_large", "The label exceeds the bounded artifact size.");
        return new(bytes, "image/svg+xml", "label-" + label.LabelId.ToString("D") + ".svg", CanonicalJson.Sha256Hex(svg.ToString()));
    }
}
