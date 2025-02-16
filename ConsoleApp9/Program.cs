using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;

class Program
{
    /// <summary>
    /// Helper: parse named switches like:
    ///   -i=Input.pdf
    ///   -o=Output.pdf
    ///   -l=72
    ///   -r=72
    ///   -t=36
    ///   -b=36
    ///   -g=12
    /// Returns a dictionary: { "i": "Input.pdf", "o": "Output.pdf", ... }
    /// </summary>
    static Dictionary<string, string> ParseSwitches(string[] args)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            // We expect something like "-i=Input.pdf" or "-l=72"
            // 1) Trim leading "-". 
            // 2) Split on '='
            // 3) Key is what's before "=", value is what's after.
            string trimmed = arg.TrimStart('-');      // e.g. "i=Input.pdf"
            int eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = trimmed.Substring(0, eqIndex).Trim();
                string val = trimmed.Substring(eqIndex + 1).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = val;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 1) CalculateGrommetPositions_Stepped
    /// Steps at exactly `spacing` until exceeding (distance - offset).
    /// This ensures "every N inches" rather than redistributing intervals.
    /// </summary>
    static List<float> CalculateGrommetPositions_Stepped(
        float totalDistance, float spacing, float offset)
    {
        var positions = new List<float>();
        if (spacing <= 0 || offset < 0 || (offset * 2) >= totalDistance)
        {
            return positions; // nothing
        }
        float current = offset;
        float maxPos = totalDistance - offset;

        // step by 'spacing' until we exceed 'maxPos'
        while (current <= maxPos + 0.0001f) // small epsilon for float rounding
        {
            positions.Add(current);
            current += spacing;
        }
        return positions;
    }

    /// <summary>
    /// 2) DrawGrommetCross
    /// Draws a small cross at (centerX, centerY). Cross size is ~1/8" physically if userUnit=1,
    /// then scaled by userUnit. 
    /// 
    /// Note: If you want a 9-point cross at userUnit=1, you do "9 * userUnit".
    /// If userUnit=1 => 9 points (1/8").
    /// 
    /// Adjust or invert if you prefer a different interpretation.
    /// </summary>
    static void DrawGrommetCross(PdfCanvas canvas, float cx, float cy, float userUnit)
    {
        // 9 points at userUnit=1 => 1/8" in standard PDF space
        float size = 9f * userUnit;
        float half = size / 2f;

        // White under-stroke (thick)
        canvas.SaveState();
        canvas.SetLineWidth(2f * userUnit);
        canvas.SetStrokeColor(ColorConstants.WHITE);
        canvas.MoveTo(cx - half, cy);
        canvas.LineTo(cx + half, cy);
        canvas.Stroke();
        canvas.MoveTo(cx, cy - half);
        canvas.LineTo(cx, cy + half);
        canvas.Stroke();
        canvas.RestoreState();

        // Black top stroke (thin)
        canvas.SaveState();
        canvas.SetLineWidth(1f * userUnit);
        canvas.SetStrokeColor(ColorConstants.BLACK);
        canvas.MoveTo(cx - half, cy);
        canvas.LineTo(cx + half, cy);
        canvas.Stroke();
        canvas.MoveTo(cx, cy - half);
        canvas.LineTo(cx, cy + half);
        canvas.Stroke();
        canvas.RestoreState();
    }

    /// <summary>
    /// 3) Main: usage with named switches
    /// </summary>
    static void Main(string[] args)
    {
        // parse the named switches
        Dictionary<string, string> switches = ParseSwitches(args);

        // required: i (input), o (output), l (left), r (right), t (top), b (bottom)
        // optional: g (grommet in inches)
        if (!switches.ContainsKey("i") || !switches.ContainsKey("o") ||
            !switches.ContainsKey("l") || !switches.ContainsKey("r") ||
            !switches.ContainsKey("t") || !switches.ContainsKey("b"))
        {
            Console.WriteLine("Usage: dotnet run -- -i=INPUT.pdf -o=OUTPUT.pdf " +
                              "-l=<leftBleedPts> -r=<rightBleedPts> -t=<topBleedPts> -b=<bottomBleedPts> " +
                              "[-g=<grommetsInInches>]");
            Console.WriteLine("Example: dotnet run -- -i=Input.pdf -o=Output.pdf -l=72 -r=72 -t=36 -b=36 -g=12");
            return;
        }

        string inputPath = switches["i"];
        string outputPath = switches["o"];

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"❌ Error: input file '{inputPath}' does not exist.");
            return;
        }

        if (!float.TryParse(switches["l"], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedLeft) ||
            !float.TryParse(switches["r"], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedRight) ||
            !float.TryParse(switches["t"], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedTop) ||
            !float.TryParse(switches["b"], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedBottom))
        {
            Console.WriteLine("❌ Error: Invalid bleed values. Must be numeric (points).");
            return;
        }

        // optional grommet spacing in inches
        float grommetInches = -1f;
        if (switches.ContainsKey("g"))
        {
            if (!float.TryParse(switches["g"], NumberStyles.Float, CultureInfo.InvariantCulture, out grommetInches))
            {
                grommetInches = -1f; // skip if invalid
            }
        }

        Console.WriteLine("=== PDF Bleed Mirroring ===");
        Console.WriteLine($"Input: {inputPath}");
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine($"Bleeds (pts): Left={bleedLeft}, Right={bleedRight}, Top={bleedTop}, Bottom={bleedBottom}");
        Console.WriteLine($"Grommet spacing (inches): {((grommetInches > 0) ? grommetInches.ToString() : "none")}");

        try
        {
            using var reader = new PdfReader(inputPath);
            using var writer = new PdfWriter(outputPath);
            using var pdfDocSrc = new PdfDocument(reader);
            using var pdfDocDest = new PdfDocument(writer);

            int totalPages = pdfDocSrc.GetNumberOfPages();
            Console.WriteLine($"Processing {totalPages} page(s)...");

            for (int i = 1; i <= totalPages; i++)
            {
                PdfPage originalPage = pdfDocSrc.GetPage(i);
                PdfDictionary origDict = originalPage.GetPdfObject();
                PdfNumber userUnitNum = origDict.GetAsNumber(PdfName.UserUnit);
                float userUnit = (userUnitNum != null) ? userUnitNum.FloatValue() : 1.0f;

                // 1) bleeds remain as typed (raw PDF points)
                // 2) for grommets, convert inches => points * userUnit
                float ptsPerInch = 72f * userUnit;
                float grommetSpacingPts = (grommetInches > 0) ? (grommetInches * ptsPerInch) : -1f;
                // e.g. -g=12 => 12 * 72 * userUnit = 864 * userUnit
                float grommetOffsetPts = 0.5f * ptsPerInch; // offset 0.5" inwards

                // final box
                Rectangle finalBox = originalPage.GetTrimBox();
                if (finalBox == null || finalBox.GetWidth() <= 0)
                    finalBox = originalPage.GetCropBox();
                if (finalBox == null || finalBox.GetWidth() <= 0)
                    finalBox = originalPage.GetMediaBox();

                float newWidth = finalBox.GetWidth() + bleedLeft + bleedRight;
                float newHeight = finalBox.GetHeight() + bleedTop + bleedBottom;

                PdfPage newPage = pdfDocDest.AddNewPage(new PageSize(newWidth, newHeight));
                newPage.GetPdfObject().Put(PdfName.UserUnit, new PdfNumber(userUnit));

                PdfCanvas canvas = new PdfCanvas(newPage);
                PdfFormXObject pageCopy = originalPage.CopyAsFormXObject(pdfDocDest);

                float offsetX = -finalBox.GetX() + bleedLeft;
                float offsetY = -finalBox.GetY() + bleedBottom;
                canvas.AddXObjectAt(pageCopy, offsetX, offsetY);

                // Mirror the content for bleeds
                MirrorBleed(canvas, pageCopy, finalBox, bleedLeft, bleedRight, bleedTop, bleedBottom, newWidth, newHeight);

                // Draw crop marks
                DrawCropMarks(canvas, finalBox, bleedLeft, bleedRight, bleedTop, bleedBottom, userUnit);

                // Draw grommets if requested
                if (grommetSpacingPts > 0)
                {
                    DrawGrommets(canvas, finalBox, bleedLeft, bleedBottom, grommetSpacingPts, grommetOffsetPts, userUnit);
                }
            }

            Console.WriteLine($"✅ Finished! Saved to {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex}");
        }
    }

    /// <summary>
    /// 4) MirrorBleed 
    /// Replicates content into the bleed margins by flipping horizontally/vertically.
    /// </summary>
    static void MirrorBleed(
        PdfCanvas canvas, PdfFormXObject pageCopy, Rectangle finalBox,
        float bleedLeft, float bleedRight, float bleedTop, float bleedBottom,
        float newWidth, float newHeight)
    {
        // LEFT
        if (bleedLeft > 0)
        {
            float leftX = 0;
            float leftY = bleedBottom;
            canvas.SaveState();
            canvas.Rectangle(leftX, leftY, bleedLeft, finalBox.GetHeight());
            canvas.Clip();
            canvas.EndPath();

            canvas.ConcatMatrix(-1, 0, 0, 1, 0, 0);
            float xOffset = -finalBox.GetX() - bleedLeft;
            float yOffset = -finalBox.GetY() + bleedBottom;
            canvas.AddXObjectAt(pageCopy, xOffset, yOffset);
            canvas.RestoreState();
        }

        // RIGHT
        if (bleedRight > 0)
        {
            float rightX = newWidth - bleedRight;
            canvas.SaveState();
            canvas.Rectangle(rightX, bleedBottom, bleedRight, finalBox.GetHeight());
            canvas.Clip();
            canvas.EndPath();

            canvas.ConcatMatrix(-1, 0, 0, 1, newWidth, 0);
            float xOffset = -finalBox.GetX() - finalBox.GetWidth() + bleedRight;
            float yOffset = -finalBox.GetY() + bleedBottom;
            canvas.AddXObjectAt(pageCopy, xOffset, yOffset);
            canvas.RestoreState();
        }

        // TOP
        if (bleedTop > 0)
        {
            float topY = newHeight - bleedTop;
            canvas.SaveState();
            canvas.Rectangle(bleedLeft, topY, finalBox.GetWidth(), bleedTop);
            canvas.Clip();
            canvas.EndPath();

            canvas.ConcatMatrix(1, 0, 0, -1, 0, newHeight);
            float xOffset = -finalBox.GetX() + bleedLeft;
            float yOffset = -finalBox.GetY() - finalBox.GetHeight() + bleedTop;
            canvas.AddXObjectAt(pageCopy, xOffset, yOffset);
            canvas.RestoreState();
        }

        // BOTTOM
        if (bleedBottom > 0)
        {
            float bottomY = 0;
            canvas.SaveState();
            canvas.Rectangle(bleedLeft, bottomY, finalBox.GetWidth(), bleedBottom);
            canvas.Clip();
            canvas.EndPath();

            canvas.ConcatMatrix(1, 0, 0, -1, 0, bleedBottom);
            float xOffset = -finalBox.GetX() + bleedLeft;
            float yOffset = -finalBox.GetY();
            canvas.AddXObjectAt(pageCopy, xOffset, yOffset);
            canvas.RestoreState();
        }
    }

    /// <summary>
    /// 5) DrawCropMarks
    /// Draw lines outside each corner. 
    /// We scale the "markLength" and line width by userUnit if we want them physically consistent.
    /// </summary>
    static void DrawCropMarks(
        PdfCanvas canvas, Rectangle finalBox,
        float bleedLeft, float bleedRight, float bleedTop, float bleedBottom,
        float userUnit)
    {
        // Let's do 20 pt base * userUnit => physically consistent
        float markLength = 20f * userUnit;
        float lineWidth = 0.5f * userUnit;

        canvas.SetLineWidth(lineWidth);
        canvas.SetStrokeColor(ColorConstants.BLACK);

        void Line(float x1, float y1, float x2, float y2)
        {
            canvas.MoveTo(x1, y1).LineTo(x2, y2).Stroke();
        }

        // bottom-left
        Line(bleedLeft, bleedBottom,
             bleedLeft - Math.Min(markLength, bleedLeft),
             bleedBottom);
        Line(bleedLeft, bleedBottom,
             bleedLeft,
             bleedBottom - Math.Min(markLength, bleedBottom));

        // bottom-right
        float brX = bleedLeft + finalBox.GetWidth();
        Line(brX, bleedBottom,
             brX + Math.Min(markLength, bleedRight),
             bleedBottom);
        Line(brX, bleedBottom,
             brX,
             bleedBottom - Math.Min(markLength, bleedBottom));

        // top-left
        float topLeftY = bleedBottom + finalBox.GetHeight();
        Line(bleedLeft, topLeftY,
             bleedLeft - Math.Min(markLength, bleedLeft),
             topLeftY);
        Line(bleedLeft, topLeftY,
             bleedLeft,
             topLeftY + Math.Min(markLength, bleedTop));

        // top-right
        float trX = bleedLeft + finalBox.GetWidth();
        float trY = bleedBottom + finalBox.GetHeight();
        Line(trX, trY,
             trX + Math.Min(markLength, bleedRight),
             trY);
        Line(trX, trY,
             trX,
             trY + Math.Min(markLength, bleedTop));
    }

    /// <summary>
    /// 6) DrawGrommets 
    /// Uses stepping so it's truly every N inches until we run out of space.
    /// </summary>
    static void DrawGrommets(
        PdfCanvas canvas,
        Rectangle finalBox,
        float bleedLeft, float bleedBottom,
        float spacingPts, float offsetPts,
        float userUnit)
    {
        float width = finalBox.GetWidth();
        float height = finalBox.GetHeight();

        // compute X positions
        List<float> xPositions = CalculateGrommetPositions_Stepped(width, spacingPts, offsetPts);
        // compute Y positions
        List<float> yPositions = CalculateGrommetPositions_Stepped(height, spacingPts, offsetPts);

        // top/bottom edges
        foreach (float x in xPositions)
        {
            // top
            DrawGrommetCross(
                canvas,
                bleedLeft + x,
                bleedBottom + height - offsetPts,
                userUnit);

            // bottom
            DrawGrommetCross(
                canvas,
                bleedLeft + x,
                bleedBottom + offsetPts,
                userUnit);
        }

        // left/right edges
        foreach (float y in yPositions)
        {
            // left
            DrawGrommetCross(
                canvas,
                bleedLeft + offsetPts,
                bleedBottom + y,
                userUnit);

            // right
            DrawGrommetCross(
                canvas,
                bleedLeft + width - offsetPts,
                bleedBottom + y,
                userUnit);
        }
    }
}
