using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.Kernel.Colors; // For color constants

class Program
{
    /// <summary>
    /// Calculate grommet positions for a given dimension using the same logic as the Python snippet.
    /// distance: The total distance (width or height) in points.
    /// desiredSpacing: The desired max spacing in points.
    /// offset: The offset from each edge in points.
    /// Returns a tuple (positions, actualSpacing) where positions is a list of measured positions from the left/top edge.
    /// If desiredSpacing <= 0, returns an empty list and 0 for actual spacing.
    /// </summary>
    static (List<float>, float) CalculateGrommetSpacing(float distance, float desiredSpacing, float offset)
    {
        // If spacing is negative or zero => skip.
        if (desiredSpacing <= 0)
        {
            return (new List<float>(), 0);
        }

        float netDistance = distance - 2 * offset;
        if (netDistance <= 0)
        {
            // If offset is too large, no grommets can be placed.
            return (new List<float>(), 0);
        }

        int intervals = (int)Math.Ceiling(netDistance / desiredSpacing);
        float actualSpacing = netDistance / intervals;

        List<float> positions = new List<float>();
        for (int i = 0; i <= intervals; i++)
        {
            positions.Add(offset + i * actualSpacing);
        }

        return (positions, actualSpacing);
    }

    /// <summary>
    /// Draws a grommet indicator (a small cross) at (centerX, centerY) with a total size of 1/8" (9 points).
    /// To ensure visibility on both dark and light backgrounds, we do a double-stroke:
    ///   1) A thicker white stroke underneath (for dark backgrounds)
    ///   2) A thinner black stroke on top (for light backgrounds)
    /// </summary>
    static void DrawGrommetCross(PdfCanvas canvas, float centerX, float centerY)
    {
        float size = 9f; // 1/8" in points
        float half = size / 2f;

        // 1) Draw a thick white stroke first
        canvas.SaveState();
        canvas.SetLineWidth(2f);
        canvas.SetStrokeColor(ColorConstants.WHITE);

        // Horizontal line
        canvas.MoveTo(centerX - half, centerY);
        canvas.LineTo(centerX + half, centerY);
        canvas.Stroke();

        // Vertical line
        canvas.MoveTo(centerX, centerY - half);
        canvas.LineTo(centerX, centerY + half);
        canvas.Stroke();
        canvas.RestoreState();

        // 2) Draw a thinner black stroke on top
        canvas.SaveState();
        canvas.SetLineWidth(1f);
        canvas.SetStrokeColor(ColorConstants.BLACK);

        // Horizontal line
        canvas.MoveTo(centerX - half, centerY);
        canvas.LineTo(centerX + half, centerY);
        canvas.Stroke();

        // Vertical line
        canvas.MoveTo(centerX, centerY - half);
        canvas.LineTo(centerX, centerY + half);
        canvas.Stroke();
        canvas.RestoreState();
    }

    static void Main(string[] args)
    {
        Console.WriteLine("=== PDF Bleed Mirroring with Crop Marks and Grommet Indicators ===");

        if (args.Length < 6)
        {
            Console.WriteLine("Usage: dotnet run <input.pdf> <output.pdf> <left-bleed> <right-bleed> <top-bleed> <bottom-bleed> [<grommet-spacing-in-inches>]");
            Console.WriteLine("Example: dotnet run input.pdf output.pdf 72 72 36 36 24");
            return;
        }

        string inputPdfPath = args[0];
        string outputPdfPath = args[1];

        // Convert all bleed & spacing from user input (in points or inches) as needed.
        if (!File.Exists(inputPdfPath))
        {
            Console.WriteLine($"❌ Error: Input file '{inputPdfPath}' does not exist.");
            return;
        }

        if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedLeft) ||
            !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedRight) ||
            !float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedTop) ||
            !float.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float bleedBottom))
        {
            Console.WriteLine("❌ Error: Invalid bleed values. Please provide numbers.");
            return;
        }

        // Optional grommet spacing in inches => convert to points (72 points = 1 inch).
        // If not provided or invalid => -1 => skip grommets.
        float grommetSpacingInches = -1f;
        if (args.Length > 6)
        {
            float temp;
            if (float.TryParse(args[6], NumberStyles.Float, CultureInfo.InvariantCulture, out temp))
            {
                grommetSpacingInches = temp;
            }
        }

        float grommetSpacingPoints = (grommetSpacingInches > 0) ? grommetSpacingInches * 72f : -1f;

        // Grommets placed 0.5" from the inside edge => 0.5" = 36 points offset.
        float grommetOffset = 36f;

        try
        {
            using (PdfReader reader = new PdfReader(inputPdfPath))
            using (PdfWriter writer = new PdfWriter(outputPdfPath))
            using (PdfDocument pdfDocSrc = new PdfDocument(reader))
            using (PdfDocument pdfDocDest = new PdfDocument(writer))
            {
                int numberOfPages = pdfDocSrc.GetNumberOfPages();
                Console.WriteLine($"Processing {numberOfPages} page(s)...");

                for (int i = 1; i <= numberOfPages; i++)
                {
                    PdfPage originalPage = pdfDocSrc.GetPage(i);
                    Rectangle originalSize = originalPage.GetPageSize();

                    float newWidth = originalSize.GetWidth() + bleedLeft + bleedRight;
                    float newHeight = originalSize.GetHeight() + bleedTop + bleedBottom;

                    // Create a new page with extra bleed area
                    PdfPage newPage = pdfDocDest.AddNewPage(new PageSize(newWidth, newHeight));
                    PdfCanvas canvas = new PdfCanvas(newPage);
                    PdfFormXObject pageCopy = originalPage.CopyAsFormXObject(pdfDocDest);

                    // Place the original page in the center of the new page
                    canvas.AddXObjectAt(pageCopy, bleedLeft, bleedBottom);

                    // --- MIRROR THE BLEED AREAS ---

                    // Left bleed
                    if (bleedLeft > 0)
                    {
                        canvas.SaveState();
                        canvas.Rectangle(0, bleedBottom, bleedLeft, originalSize.GetHeight());
                        canvas.Clip();
                        canvas.EndPath();
                        canvas.ConcatMatrix(-1, 0, 0, 1, bleedLeft, 0); // horizontal flip
                        canvas.AddXObjectAt(pageCopy, -bleedLeft, bleedBottom);
                        canvas.RestoreState();
                    }

                    // Right bleed
                    if (bleedRight > 0)
                    {
                        float rightX = newWidth - bleedRight;
                        canvas.SaveState();
                        canvas.Rectangle(rightX, bleedBottom, bleedRight, originalSize.GetHeight());
                        canvas.Clip();
                        canvas.EndPath();
                        canvas.ConcatMatrix(-1, 0, 0, 1, newWidth, 0); // horizontal flip
                        canvas.AddXObjectAt(pageCopy, -(originalSize.GetWidth() - bleedRight), bleedBottom);
                        canvas.RestoreState();
                    }

                    // Top bleed
                    if (bleedTop > 0)
                    {
                        float topY = newHeight - bleedTop;
                        canvas.SaveState();
                        canvas.Rectangle(bleedLeft, topY, originalSize.GetWidth(), bleedTop);
                        canvas.Clip();
                        canvas.EndPath();
                        canvas.ConcatMatrix(1, 0, 0, -1, 0, newHeight); // vertical flip
                        canvas.AddXObjectAt(pageCopy, bleedLeft, -(originalSize.GetHeight() - bleedTop));
                        canvas.RestoreState();
                    }

                    // Bottom bleed
                    if (bleedBottom > 0)
                    {
                        canvas.SaveState();
                        canvas.Rectangle(bleedLeft, 0, originalSize.GetWidth(), bleedBottom);
                        canvas.Clip();
                        canvas.EndPath();
                        canvas.ConcatMatrix(1, 0, 0, -1, 0, bleedBottom); // vertical flip
                        canvas.AddXObjectAt(pageCopy, bleedLeft, 0);
                        canvas.RestoreState();
                    }

                    // === DRAW LINES ONLY IN THE BLEED AREA (CORNER MARKS) ===

                    float markLength = 20f; // length of corner lines in bleed area

                    // BOTTOM-LEFT boundary corner
                    float blX = bleedLeft;
                    float blY = bleedBottom;

                    // horizontal line going left into bleed
                    if (bleedLeft > 0)
                    {
                        canvas.MoveTo(blX, blY);
                        canvas.LineTo(blX - Math.Min(markLength, bleedLeft), blY);
                        canvas.Stroke();
                    }
                    // vertical line going down into bleed
                    if (bleedBottom > 0)
                    {
                        canvas.MoveTo(blX, blY);
                        canvas.LineTo(blX, blY - Math.Min(markLength, bleedBottom));
                        canvas.Stroke();
                    }

                    // BOTTOM-RIGHT boundary corner
                    float brX = bleedLeft + originalSize.GetWidth();
                    float brY = bleedBottom;

                    // horizontal line going right into bleed
                    if (bleedRight > 0)
                    {
                        canvas.MoveTo(brX, brY);
                        canvas.LineTo(brX + Math.Min(markLength, bleedRight), brY);
                        canvas.Stroke();
                    }
                    // vertical line going down into bleed
                    if (bleedBottom > 0)
                    {
                        canvas.MoveTo(brX, brY);
                        canvas.LineTo(brX, brY - Math.Min(markLength, bleedBottom));
                        canvas.Stroke();
                    }

                    // TOP-LEFT boundary corner
                    float tlX = bleedLeft;
                    float tlY = bleedBottom + originalSize.GetHeight();

                    // horizontal line going left into bleed
                    if (bleedLeft > 0)
                    {
                        canvas.MoveTo(tlX, tlY);
                        canvas.LineTo(tlX - Math.Min(markLength, bleedLeft), tlY);
                        canvas.Stroke();
                    }
                    // vertical line going up into bleed
                    if (bleedTop > 0)
                    {
                        canvas.MoveTo(tlX, tlY);
                        canvas.LineTo(tlX, tlY + Math.Min(markLength, bleedTop));
                        canvas.Stroke();
                    }

                    // TOP-RIGHT boundary corner
                    float trX = bleedLeft + originalSize.GetWidth();
                    float trY = bleedBottom + originalSize.GetHeight();

                    // horizontal line going right into bleed
                    if (bleedRight > 0)
                    {
                        canvas.MoveTo(trX, trY);
                        canvas.LineTo(trX + Math.Min(markLength, bleedRight), trY);
                        canvas.Stroke();
                    }
                    // vertical line going up into bleed
                    if (bleedTop > 0)
                    {
                        canvas.MoveTo(trX, trY);
                        canvas.LineTo(trX, trY + Math.Min(markLength, bleedTop));
                        canvas.Stroke();
                    }

                    // === DRAW GROMMETS if spacing is > 0 ===
                    // We'll consider the "banner" as the original content area.
                    // We want them placed 0.5" (36 points) inwards from the inside border.
                    // So effectively, the banner's workable dimension is: (originalWidth - 2*grommetOffset) x (originalHeight - 2*grommetOffset),
                    // but the positions returned by CalculateGrommetSpacing are from 0..(width or height). We'll offset them.

                    if (grommetSpacingPoints > 0)
                    {
                        float bannerWidth = originalSize.GetWidth();
                        float bannerHeight = originalSize.GetHeight();

                        // Calculate X positions:
                        (List<float> xPositions, float xActualSpacing) = CalculateGrommetSpacing(
                            bannerWidth, grommetSpacingPoints, grommetOffset);

                        // Calculate Y positions:
                        (List<float> yPositions, float yActualSpacing) = CalculateGrommetSpacing(
                            bannerHeight, grommetSpacingPoints, grommetOffset);

                        // Draw grommets ONLY on the perimeter, not inside the image
                        foreach (float xPos in xPositions)
                        {
                            float gx = bleedLeft + xPos;

                            // Top Edge (0.5" from top of artboard)
                            DrawGrommetCross(canvas, gx, bleedBottom + originalSize.GetHeight() - grommetOffset);

                            // Bottom Edge (0.5" from bottom of artboard)
                            DrawGrommetCross(canvas, gx, bleedBottom + grommetOffset);
                        }

                        foreach (float yPos in yPositions)
                        {
                            float gy = bleedBottom + yPos;

                            // Left Edge (0.5" from left of artboard)
                            DrawGrommetCross(canvas, bleedLeft + grommetOffset, gy);

                            // Right Edge (0.5" from right of artboard)
                            DrawGrommetCross(canvas, bleedLeft + originalSize.GetWidth() - grommetOffset, gy);
                        }

                    }

                } // end for each page

                Console.WriteLine($"✅ Successfully processed PDF! Output saved to: {outputPdfPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex}");
        }
    }
}
