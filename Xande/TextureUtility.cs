using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data.Parsing;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Xande {
    public class TextureUtility {
        public static IReadOnlyList<(TextureUsage, Bitmap)> ComputeCharacterModelTextures( Lumina.Models.Materials.Material xivMaterial, BitmapData normal, BitmapData? initDiffuse ) {
            var diffuse = new Bitmap( normal.Width, normal.Height, PixelFormat.Format32bppArgb );
            var specular = new Bitmap( normal.Width, normal.Height, PixelFormat.Format32bppArgb );
            var emission = new Bitmap( normal.Width, normal.Height, PixelFormat.Format32bppArgb );

            var colorSetInfo = xivMaterial.File!.ColorSetInfo;

            // copy alpha from normal to original diffuse if it exists
            if( initDiffuse != null ) {
                CopyNormalBlueChannelToDiffuseAlphaChannel( normal, initDiffuse );
            }

            for( var x = 0; x < normal.Width; x++ ) {
                for( var y = 0; y < normal.Height; y++ ) {
                    var normalPixel = GetPixel( normal, x, y );

                    var colorSetIndex1 = normalPixel.A / 17 * 16;
                    var colorSetBlend = normalPixel.A % 17 / 17.0;
                    var colorSetIndexT2 = normalPixel.A / 17;
                    var colorSetIndex2 = ( colorSetIndexT2 >= 15 ? 15 : colorSetIndexT2 + 1 ) * 16;

                    // to fix transparency issues 
                    // normal.SetPixel( x, y, Color.FromArgb( normalPixel.B, normalPixel.R, normalPixel.G, 255 ) );
                    SetPixel( normal, x, y, Color.FromArgb( normalPixel.B, normalPixel.R, normalPixel.G, 255 ) );

                    var diffuseBlendColour = ColorUtility.BlendColorSet( in colorSetInfo, colorSetIndex1, colorSetIndex2, normalPixel.B, colorSetBlend, ColorUtility.TextureType.Diffuse );
                    var specularBlendColour = ColorUtility.BlendColorSet( in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend, ColorUtility.TextureType.Specular );
                    var emissionBlendColour = ColorUtility.BlendColorSet( in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend, ColorUtility.TextureType.Emissive );

                    // Set the blended colors in the respective bitmaps
                    diffuse.SetPixel( x, y, diffuseBlendColour );
                    specular.SetPixel( x, y, specularBlendColour );
                    emission.SetPixel( x, y, emissionBlendColour );
                }
            }

            return new List<(TextureUsage, Bitmap)> {
                ( TextureUsage.SamplerDiffuse, diffuse ),
                ( TextureUsage.SamplerSpecular, specular ),
                ( TextureUsage.SamplerReflection, emission ),
            };
        }

        public static void CopyNormalBlueChannelToDiffuseAlphaChannel( BitmapData normal, BitmapData diffuse ) {
            // need to scale normal map lookups to diffuse size since the maps are usually smaller
            // will look blocky but its better than nothing
            var scaleX = ( float )diffuse.Width / normal.Width;
            var scaleY = ( float )diffuse.Height / normal.Height;

            for( var x = 0; x < diffuse.Width; x++ ) {
                for( var y = 0; y < diffuse.Height; y++ ) {
                    //var diffusePixel = diffuse.GetPixel( x, y );
                    //var normalPixel = normal.GetPixel( ( int )( x / scaleX ), ( int )( y / scaleY ) );
                    //diffuse.SetPixel( x, y, Color.FromArgb( normalPixel.B, diffusePixel.R, diffusePixel.G, diffusePixel.B ) );

                    var diffusePixel = GetPixel( diffuse, x, y );
                    var normalPixel = GetPixel( normal, ( int )( x / scaleX ), ( int )( y / scaleY ) );

                    SetPixel( diffuse, x, y, Color.FromArgb( normalPixel.B, diffusePixel.R, diffusePixel.G, diffusePixel.B ) );
                }
            }
        }

        public static void SetPixel( BitmapData data, int x, int y, Color color ) {
            try {
                if( x < 0 || x >= data.Width || y < 0 || y >= data.Height ) {
                    throw new ArgumentOutOfRangeException( nameof(x), nameof(y), "x or y is out of bounds" );
                }

                int bytesPerPixel = Image.GetPixelFormatSize( data.PixelFormat ) / 8;
                int offset = y * data.Stride + x * bytesPerPixel;

                if( offset < 0 || offset + bytesPerPixel > data.Stride * data.Height ) {
                    throw new ArgumentOutOfRangeException( "Memory access error" );
                }

                var pixel = new byte[bytesPerPixel];
                Marshal.Copy( data.Scan0 + offset, pixel, 0, bytesPerPixel );

                pixel[0] = color.B; // Blue
                pixel[1] = color.G; // Green
                pixel[2] = color.R; // Red

                if( bytesPerPixel == 4 ) {
                    pixel[3] = color.A; // Alpha
                }

                Marshal.Copy( pixel, 0, data.Scan0 + offset, bytesPerPixel );
            }
            catch( ArgumentOutOfRangeException ex ) {
                Console.WriteLine( "Error: " + ex.Message );
            }
        }

        public static Color GetPixel( BitmapData data, int x, int y, IPluginLog? logger = null ) {
            try {
                if( x < 0 || x >= data.Width || y < 0 || y >= data.Height ) {
                    throw new ArgumentOutOfRangeException( nameof(x), nameof(y), "x or y is out of bounds" );
                }

                int bytesPerPixel = Image.GetPixelFormatSize( data.PixelFormat ) / 8;
                int offset = y * data.Stride + x * bytesPerPixel;

                if( offset < 0 || offset + bytesPerPixel > data.Stride * data.Height ) {
                    throw new InvalidOperationException( "Memory access error" );
                }

                var pixel = new byte[bytesPerPixel];
                Marshal.Copy( data.Scan0 + offset, pixel, 0, bytesPerPixel );

                if( bytesPerPixel == 4 ) {
                    return Color.FromArgb( pixel[3], pixel[2], pixel[1], pixel[0] );
                }
                else if( bytesPerPixel == 3 ) {
                    return Color.FromArgb( 255, pixel[2], pixel[1], pixel[0] );
                }
                else {
                    throw new InvalidOperationException( "Unsupported pixel format" );
                }
            }
            catch( ArgumentOutOfRangeException ex ) {
                logger?.Error( ex, ex.Message );
                return Color.Transparent;
            }
            catch( InvalidOperationException ex ) {
                logger?.Error( ex, ex.Message );
                return Color.Transparent;
            }
        }


        public static Bitmap ComputeOcclusion( BitmapData mask, BitmapData specularMap ) {
            var occlusion = new Bitmap( mask.Width, mask.Height, PixelFormat.Format32bppArgb );

            for( var x = 0; x < mask.Width; x++ ) {
                for( var y = 0; y < mask.Height; y++ ) {
                    var maskPixel = GetPixel( mask, x, y );
                    var specularPixel = GetPixel( specularMap, x, y );

                    // Calculate the new RGB channels for the specular pixel based on the mask pixel
                    SetPixel( specularMap, x, y, Color.FromArgb(
                        specularPixel.A,
                        Convert.ToInt32( specularPixel.R * Math.Pow( maskPixel.G / 255.0, 2 ) ),
                        Convert.ToInt32( specularPixel.G * Math.Pow( maskPixel.G / 255.0, 2 ) ),
                        Convert.ToInt32( specularPixel.B * Math.Pow( maskPixel.G / 255.0, 2 ) )
                    ) );

                    // Oops all red
                    occlusion.SetPixel( x, y, Color.FromArgb(
                        255,
                        maskPixel.R,
                        maskPixel.R,
                        maskPixel.R
                    ) );
                }
            }

            return occlusion;
        }
    }
}
