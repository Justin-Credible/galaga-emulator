
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace JustinCredible.GalagaEmu.Tests
{
    public class TileRendererTests
    {
        // [Theory]
        // [InlineData(1, "render-all-tiles-palette-1.bmp")]
        // [InlineData(2, "render-all-tiles-palette-2.bmp")]
        // [InlineData(8, "render-all-tiles-palette-8.bmp")]
        public void TestRenderTileRendersAllTiles(int paletteIndex, string fileToCompare)
        {
            var romData = new ROMData();
            romData.Data[ROMs.NAMCO_REV_B_PALETTE.ID] = VideoHardwareTestData.COLOR_ROM;
            romData.Data[ROMs.NAMCO_REV_B_CHAR_LOOKUP.ID] = VideoHardwareTestData.CHAR_PALETTE_ROM;
            romData.Data[ROMs.NAMCO_REV_B_SPRITE_LOOKUP.ID] = null;
            romData.Data[ROMs.NAMCO_REV_B_GFX1.ID] = null;
            romData.Data[ROMs.NAMCO_REV_B_GFX2.ID] = null;
            romData.Data[ROMs.NAMCO_REV_B_GFX3.ID] = null;

            var video = new VideoHardware(romData);
            video.InitializeColors();
            video.InitializePalettes();

            var tileRenderer = new TileRenderer(VideoHardwareTestData.TILE_ROM, video._charPallets);

            // The width in pixels between each tile; set to 1 to match MAME's palette tool from which
            // the reference screenshots are taken. The background color also matches MAME's UI.
            var borderPixels = 1;
            var bgColor = new Rgba32(15, 15, 39);

            // There are 256 tiles, so we can render a grid of 16x16 tiles.
            // Each tile is 8x8 pixels.
            var width = 16 * (8 + borderPixels);
            var height = 16 * (8 + borderPixels);

            // Holds the (x, y) coordinates of the origin (top/left) of the location
            // to render the next tile.
            var tileOriginX = 0;
            var tileOriginY = 0;

            // The image we'll be rendering all the tiles to.
            var image = new Image<Rgba32>(width, height, bgColor);

            // Render each of the 256 tiles.
            for (var tileIndex = 0; tileIndex < 256; tileIndex++)
            {
                // Render the tile with the given color palette.
                var tile = tileRenderer.RenderTile(tileIndex, paletteIndex);

                // Copy the rendered tile over into the full image.
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        image[tileOriginX + x, tileOriginY + y] = tile[x, y];
                    }
                }

                if ((tileIndex + 1) % 16 == 0)
                {
                    // Row is finished, wrap back around.
                    tileOriginX = 0;
                    tileOriginY += 8 + borderPixels;
                }
                else
                {
                    // Next column.
                    tileOriginX += 8 + borderPixels;
                }
            }

            // Assert: the rendered image should be the same as the reference image.

            byte[] actualBytes = null;

            using (var steam = new MemoryStream())
            {
                image.Save(steam, new BmpEncoder());
                actualBytes = steam.ToArray();
            }

            var expectedBytes = File.ReadAllBytes($"../../../ReferenceData/{fileToCompare}");

            Assert.Equal(expectedBytes, actualBytes);
        }
    }
}
