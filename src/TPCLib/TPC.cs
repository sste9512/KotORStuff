﻿
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TPCLib
{
    // Problem files:
    // //d2/Media/GameData/KotOR/ERFs/TexturePacks/swpc_tex_gui.erf:
    //   --    2    2  1  1  1 RGB  i_equipm.tpc      : Crash, parameter is not valid.
    //   --    2    2  1  1  1 RGB  cmp_dot.tpc       : Crash, parameter is not valid.
    //   --    2    2  1  1  1 RGB  i_useitemm.tpc    : Crash, parameter is not valid.
    //   --    2    2  1  1  1 RGB  i_powerm.tpc      : Crash, parameter is not valid.
    //   --    2    2  1  1  1 RGB  i_featm.tpc       : Crash, parameter is not valid.
    //   --    2    2  1  1  1 RGB  i_attackm.tpc     : Crash, parameter is not valid.
    //   z-  512  256  1  1  1 RGBA lbl_mapm46aa.tpc  : White?
    //   z-  512  256  1  1  1 RGBA LBL_mapM50aa.tpc  : Gray right edge?
    //
    // //d2/Media/GameData/KotOR/ERFs/TexturePacks/swpc_tex_tpa.erf:
    //   --  256  256  1  1  1 RGBA 3dgui_a00001.tpc  : Funky?
    //
    // //d2/Media/GameData/KotOR/ERFs/TexturePacks/swpc_tex_tpb.erf:
    //   --    2    2  1  2  2 RGB  LMG_Grad01.tpc    : Crash, parameter is not valid.
    //
    // //d2/Media/GameData/KotOR/ERFs/TexturePacks/swpc_tex_tpc.erf:
    //   --    2    2  1  2  2 RGB  LMG_Grad01.tpc    : Crash, parameter is not valid.
    //
    // //d2/Media/GameData/KotOR-TSL/ERFs/TexturePacks/swpc_tex_gui.erf:
    //   --    2    2  1  1  1 RGB  i_attackm.tpc     : Crash, parameter is not valid.
    //
    // //d2/Media/GameData/KotOR-TSL/ERFs/TexturePacks/swpc_tex_tpa.erf:
    //   --  512  512  1  1  1 RGBA PLCaa_a00005.tpc  : Incorrect width?
    //
    // //d2/Media/GameData/KotOR-TSL/ERFs/TexturePacks/swpc_tex_tpb.erf:
    //   --    4    4  1  3  3 RGB  LMG_Grad01.tpc    : Crash, parameter is not valid.
    //
    // //d2/Media/GameData/KotOR-TSL/ERFs/TexturePacks/swpc_tex_tpc.erf:
    //   --    4    4  1  3  3 RGB  LMG_Grad01.tpc    : Crash, parameter is not valid.
    //   z-    8    8  1  4  4 RGB  MG_Line.tpc       : Crash, parameter is not valid.
    //
    //

    public class TPC
    {
        public static readonly bool DEBUG = false;

        #region Types

        public class Image
        {
            public List<SubImage> SubImages = new List<SubImage>();
        }

        public class SubImage
        {
            public UInt16 Width;
            public UInt16 Height;
            public UInt16 CanvasWidth;   // Takes quantization into account (DXT images are quantized at 4 pixels)
            public UInt16 CanvasHeight;  // Takes quantization into account (DXT images are quantized at 4 pixels)
            public UInt32 DataSize;
            public byte[] Data;
        }

        public enum EncodingFormat
        {
            Gray = 0x01,
            RGB  = 0x02,
            RGBA = 0x04,
            BGRA = 0x0C
        }
        #endregion Types

        #region Fields

        #region "Read from file"

        /// <summary>
        /// The data size field from the TPC file.
        /// If the value is zero, the images are uncompressed and the size must be
        /// computed from other values.
        /// 
        /// If the value is not zero, the images are compressed with DXT. Typically
        /// this value then represents the number of bytes required to define the
        /// largest sub image in the file.
        /// 
        /// In the case of cycle procedurals, this value specifies the full size of
        /// all frames and the Width, Height and SubImageCount fields do NOT
        /// represent the individual frames.
        /// </summary>
        public UInt32 ReadDataSize;

        /// <summary>
        /// Float of unknown purpose. Mostly this is 1f, but other values are used in KotOR and TSL.
        /// </summary>
        public float Unknown1 = 1f;

        /// <summary>
        /// The width, in number of pixels, of the largest image in the file.
        /// </summary>
        public UInt16 Width;

        /// <summary>
        /// The height, in number of pixels, of the largest image in the file.
        /// </summary>
        public UInt16 Height;

        /// <summary>
        /// Specifies how many colour channels are used in the images. It also specifies the respective ordering of the channel data.
        /// </summary>
        public EncodingFormat Format;

        /// <summary>
        /// The number of mip maps. Each consecutive sub image is reduced in half on width and height. The first sub image is actually the full size image.
        /// </summary>
        public byte SubImageCount;
        #endregion "Read from file"

        /// <summary>
        /// The number of bytes between the header and the TXI section. A computed value.
        /// </summary>
        public UInt32 TotalDataSize;

        /// <summary>
        /// The number of bytes for the base image. This value is computed, either from DataSize or the dimensions and format of the image.
        /// </summary>
        public UInt32 BaseImageDataSize;

        public byte ImageCount;
        public byte totalSubImageCount;
        public float BytesPerPixel;

        /// <summary>
        /// The smallest number of pixels allowed in any direction. DXT compression requires that with and height are multiples of four.
        /// </summary>
        public uint Quantization;

        public byte[] PixelData;
        public List<Image> Images = new List<Image>();

        public bool isCubeMap;
        public bool isCompressed;

        public TXIValueDictionary TXI;

        public long streamSize;
        #endregion Fields

        #region Properties
        public Bitmap this[int imageIndex, int subImageIndex]
        {
            get
            {
                if (imageIndex < 0 || imageIndex >= Images.Count)
                {
                    throw new Exception("TPC image index out of range.");
                }
                Image image = Images[imageIndex];
                if (subImageIndex < 0 || subImageIndex >= image.SubImages.Count)
                {
                    throw new Exception("TPC sub image index out of range.");
                }
                SubImage subImage = image.SubImages[subImageIndex];

                PixelFormat format = PixelFormat.Undefined;
                int stride = 0;
                byte[] buf = null;

                EncodingFormat encoding = Format;

                if (isCompressed && encoding == EncodingFormat.RGB)
                {
                    encoding = EncodingFormat.RGBA; // RGB/DXT1 images has been decompressed into an RGBA image
                }

                switch (encoding)
                {
                    case EncodingFormat.Gray:
                        format = PixelFormat.Format24bppRgb;
                        stride = subImage.CanvasWidth * 3;
                        buf = new byte[subImage.DataSize * 3];

                        for (int i = 0; i < subImage.DataSize; i++)
                        {
                            buf[i * 3 + 0] = subImage.Data[i];
                            buf[i * 3 + 1] = subImage.Data[i];
                            buf[i * 3 + 2] = subImage.Data[i];
                        }
                        break;
                    case EncodingFormat.RGB:
                        format = PixelFormat.Format24bppRgb;
                        stride = subImage.CanvasWidth * 3;
                        buf = new byte[subImage.DataSize];
                        for (int y = 0; y < subImage.CanvasHeight; y++)
                        {
                            for (int x = 0; x < subImage.CanvasWidth; x++)
                            {
                                int i = (y * subImage.CanvasWidth + x) * 3;
                                int j = ((subImage.CanvasHeight - y - 1) * subImage.CanvasWidth + x) * 3;
                                buf[i + 0] = subImage.Data[j + 2];
                                buf[i + 1] = subImage.Data[j + 1];
                                buf[i + 2] = subImage.Data[j + 0];
                            }
                        }
                        break;
                    case EncodingFormat.RGBA:
                        format = PixelFormat.Format32bppArgb;
                        stride = subImage.CanvasWidth * 4;
                        buf = new byte[subImage.DataSize];
                        for (int y = 0; y < subImage.CanvasHeight; y++)
                        {
                            for (int x = 0; x < subImage.CanvasWidth; x++)
                            {
                                int i = (y * subImage.CanvasWidth + x) * 4;
                                int j = ((subImage.CanvasHeight - y - 1) * subImage.CanvasWidth + x) * 4;
                                buf[i + 0] = subImage.Data[j + 2];
                                buf[i + 1] = subImage.Data[j + 1];
                                buf[i + 2] = subImage.Data[j + 0];
                                buf[i + 3] = subImage.Data[j + 3];
                            }
                        }
                        break;
                    default:
                        throw new Exception(string.Format("Can't convert from format {0}.", Format));
                }
                if (buf == null)
                {
                    throw new Exception(string.Format("No byte source?!?."));
                }

                GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                IntPtr pinnedBuf = handle.AddrOfPinnedObject();
                Bitmap tmp = new Bitmap(subImage.CanvasWidth, subImage.CanvasHeight, stride, format, pinnedBuf);
                Bitmap bitmap = new Bitmap(tmp); // Make sure that we copy the data so that buf can be garbage collected safely.
                handle.Free();

                return bitmap;
            }
        }

        #endregion Properties

        #region Construction
        public TPC(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                streamSize = stream.Length;

                using (BinaryReader reader = new BinaryReader(stream))
                {
                    try
                    {
                        ReadHeader(reader);
                        ParseHeader();
                        PixelData = reader.ReadBytes((int)TotalDataSize);
                        readTXIData(reader);
                        DefineImages();
                        ParsePixelData();

                        //fixupCubeMap();

                    }
                    catch (Exception e)
                    {
                        throw new Exception("Failed reading TPC file", e);
                    }

                    // TODO: Should we leave the sub images compressed and only decompress them when someone is asking for a System.Drawing.Bitmap version?
                    Decompress();

                }
            }
        }

        public TPC(bool isCompressed, EncodingFormat format, int subImageCount)
        {
            this.isCompressed = isCompressed;
            this.Format = format;
            this.SubImageCount = (byte)subImageCount;

            if (isCompressed)
            {
                throw new Exception("DXT compression is not yet supported for new TPCs.");
            }

            // Set up pixel format details:
            DeterminePixelFormat();

            TXI = new TXIValueDictionary();
        }

        #endregion Construction

        #region ReadTPC
        protected void ReadHeader(BinaryReader reader)
        {
            ReadDataSize = reader.ReadUInt32();  // Number of bytes for the compressed pixel data in one full image or zero if the image is not compressed
            Unknown1 = reader.ReadSingle();

            Width = reader.ReadUInt16();    // Maximum 0x8000?
            Height = reader.ReadUInt16();   // Maximum 0x8000?

            Format = (EncodingFormat)reader.ReadByte(); // TODO: Catch error
            SubImageCount = reader.ReadByte();

            reader.ReadBytes(114);                 // Unknown
        }

        protected void ParseHeader()
        {
            if ((Width >= 0x8000) || (Height >= 0x8000))
            {
                throw new Exception(string.Format("Unsupported image dimensions ({0}x{1})", Width, Height));
            }

            isCompressed = (ReadDataSize != 0);
            DeterminePixelFormat();
            BaseImageDataSize = GetDataSizeOfSubImage(Width, Height, BytesPerPixel, Quantization);

            // Determine the TotalDataSize:
            if (ReadDataSize > BaseImageDataSize && SubImageCount == 1) // TODO: This test isn't robust. It handles cycle procedure images but feels wrong.
            {
                TotalDataSize = ReadDataSize;
            }
            else
            {
                if ((Height % 6) == 0) // FIXME: This is an ugly hack to enable reading of six-sided cube maps
                {
                    isCubeMap = true;
                    Height /= 6;
                }

                TotalDataSize = 0;
                int w = Width;
                int h = Height;
                int i = 0;
                // TODO: What is the proper condition for when to ignore the SubImageCount and go on until 0x0?
                while ((i < SubImageCount) || (isCompressed && !(w == 0 && h == 0)))
                {
                    TotalDataSize += GetDataSizeOfSubImage(w, h, BytesPerPixel, Quantization);
                    w >>= 1;
                    h >>= 1;
                    i++;
                }

                if (isCubeMap) // FIXME: This is an ugly hack to enable reading of six-sided cube maps
                {
                    TotalDataSize *= 6;
                    Height *= 6;
                }
            }
        }

        protected void DeterminePixelFormat()
        {
            if (!isCompressed)
            {
                switch (Format)
                {
                    case EncodingFormat.Gray:
                        BytesPerPixel = 1f;
                        Quantization = 1;
                        BaseImageDataSize = (uint)(Width * Height);
                        break;
                    case EncodingFormat.RGB:
                        BytesPerPixel = 3f;
                        Quantization = 1;
                        BaseImageDataSize = (uint)(Width * Height * 3);
                        break;
                    case EncodingFormat.RGBA:
                        BytesPerPixel = 4f;
                        Quantization = 1;
                        BaseImageDataSize = (uint)(Width * Height * 4);
                        break;
                    case EncodingFormat.BGRA:
                        BytesPerPixel = 4f;
                        Quantization = 1;
                        BaseImageDataSize = (uint)(Width * Height * 4);
                        break;
                    default:
                        throw new Exception(string.Format("Unknown TPC raw encoding: {0} ({1}), {2}x{3}, {4}", Format, ReadDataSize, Width, Height, (uint)SubImageCount));
                }
            }
            else
            {
                // Compressed:
                switch (Format)
                {
                    case EncodingFormat.RGB: // S3TC DXT1
                        BytesPerPixel = 8f / 16; // 8 bytes per 16 pixels
                        Quantization = 4;
                        break;
                    case EncodingFormat.RGBA: // S3TC DXT5
                        BytesPerPixel = 16f / 16; // 16 bytes per 16 pixels
                        Quantization = 4;
                        break;
                    default:
                        throw new Exception(string.Format("Unknown TPC encoding: {0} ({1})", Format, BaseImageDataSize));
                }
            }
        }

        public UInt32 GetDataSizeOfSubImage(int width, int height, float bytesPerPixel, uint quantization)
        {
            UInt16 canvasWidth  = (UInt16)(Math.Ceiling((float)Math.Max(width,  (UInt16)1) / quantization) * quantization);
            UInt16 canvasHeight = (UInt16)(Math.Ceiling((float)Math.Max(height, (UInt16)1) / quantization) * quantization);
            return (UInt32)Math.Ceiling(canvasWidth * canvasHeight * bytesPerPixel);
        }

        protected void DefineImages()
        {
            ImageCount = 1;

            if (TXI["cube"] == "1")
            {
                //TODO: Some "cube maps" have only four sides
                isCubeMap = true;
                Height /= 6;
                ImageCount = 6;
            }

            if (TXI["proceduretype"] == "cycle")
            {
                UInt16 numx = (TXI["numx"] == "") ? (UInt16)0 : UInt16.Parse(TXI["numx"]);
                UInt16 numy = (TXI["numy"] == "") ? (UInt16)0 : UInt16.Parse(TXI["numy"]);
                if (numx <= 0 || numy <= 0)
                {
                    throw new Exception("numx and numy must be greater than zero in a cycle procedure.");
                }
                Width /= numx;
                Height /= numy;
                ImageCount = (byte)(numx * numy);
                SubImageCount = (byte)(Math.Min(intLog2(Width), intLog2(Height)) + 1);
            }


            totalSubImageCount = 0;
            for (int imageIndex = 0; imageIndex < ImageCount; imageIndex++)
            {
                Image image = new Image();
                Images.Add(image);

                UInt32 subImageWidth  = (UInt32)Math.Ceiling((float)Width  / Quantization) * Quantization;
                UInt32 subImageHeight = (UInt32)Math.Ceiling((float)Height / Quantization) * Quantization;

                for (int i = 0; i < SubImageCount; i++)
                {
                    SubImage subImage = new SubImage();

                    subImage.Width        = (UInt16)subImageWidth;
                    subImage.Height       = (UInt16)subImageHeight;
                    subImage.CanvasWidth  = (UInt16)(Math.Ceiling((float)Math.Max(subImage.Width,  (UInt16)1) / Quantization) * Quantization);
                    subImage.CanvasHeight = (UInt16)(Math.Ceiling((float)Math.Max(subImage.Height, (UInt16)1) / Quantization) * Quantization);
                    subImage.DataSize     = (UInt32)Math.Ceiling(subImage.CanvasWidth * subImage.CanvasHeight * BytesPerPixel);

                     //Debug(string.Format("Mipmap {0}: {1}x{2} {3}", i, subImageWidth, subImageHeight, subImage.DataSize));

                    image.SubImages.Add(subImage);
                    totalSubImageCount++;

                    subImageWidth >>= 1;
                    subImageHeight >>= 1;
                }
            }
        }

        protected void ParsePixelData()
        {
            BinaryReader reader = new BinaryReader(new MemoryStream(PixelData));
            foreach (Image image in Images)
            {
                foreach (SubImage subImage in image.SubImages)
                {
                    // If the texture width is a power of two, the texture memory layout is "swizzled"
                    bool widthPOT = (subImage.Width & (subImage.Width - 1)) == 0;
                    bool swizzled = (Format == EncodingFormat.BGRA) && widthPOT;

                    byte[] tmp = reader.ReadBytes((int)subImage.DataSize);
                    if (tmp.GetLength(0) != subImage.DataSize)
                    {
                        throw new Exception("Read error.");
                    }

                    if (swizzled)
                    {
                        subImage.Data = new byte[subImage.DataSize];
                        DeSwizzle(subImage.Data, tmp, subImage.CanvasWidth, subImage.CanvasHeight);
                    }
                    else
                    {
                        subImage.Data = tmp;
                    }
                }
            }
            reader.Close();
        }

        #region Swizzling

        void DeSwizzle(byte[] dst, byte[] src, UInt32 width, UInt32 height)
        {
            int dstIndex = 0;
            for (UInt32 y = 0; y < height; y++)
            {
                for (UInt32 x = 0; x < width; x++)
                {
                    UInt32 offset = DeSwizzleOffset(x, y, width, height) * 4;

                    dst[dstIndex++] = src[offset + 0];
                    dst[dstIndex++] = src[offset + 1];
                    dst[dstIndex++] = src[offset + 2];
                    dst[dstIndex++] = src[offset + 3];
                }
            }
        }

        /** De-"swizzle" a texture pixel offset. */
        static UInt32 DeSwizzleOffset(UInt32 x, UInt32 y, UInt32 width, UInt32 height)
        {
            width = intLog2(width);
            height = intLog2(height);

            UInt32 offset = 0;
            int shiftCount = 0;

            while ((width | height) != 0)
            {
                if (width != 0)
                {
                    offset |= (x & 0x01) << shiftCount;

                    x >>= 1;

                    shiftCount++;
                    width--;
                }

                if (height != 0)
                {
                    offset |= (y & 0x01) << shiftCount;

                    y >>= 1;

                    shiftCount++;
                    height--;
                }
            }

            return offset;
        }

        static readonly UInt32[] log2Table =
        {
            0,  9,  1, 10, 13, 21,  2, 29,
            11, 14, 16, 18, 22, 25,  3, 30,
            8, 12, 20, 28, 15, 17, 24,  7,
            19, 27, 23,  6, 26,  5,  4, 31
        };

        static UInt32 intLog2(UInt32 value)
        {
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return log2Table[(UInt32)(value * 0x07C4ACDD) >> 27];
        }
        #endregion Swizzling

        private void readTXIData(BinaryReader reader)
        {
            using (var ms = new MemoryStream())
            {
                reader.BaseStream.CopyTo(ms);
                byte[] buf = ms.ToArray();
                string s = System.Text.Encoding.ASCII.GetString(buf);
                TXI = new TXIValueDictionary (s.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        protected void Decompress()
        {
            if (!isCompressed)
                return;

            foreach (Image image in Images)
            {
                foreach (SubImage subImage in image.SubImages)
                {
                    DXT.Format format;
                    switch (Format)
                    {
                        case EncodingFormat.RGB:
                            format = DXT.Format.DXT1;
                            break;
                        case EncodingFormat.RGBA:
                            format = DXT.Format.DXT5;
                            break;
                        default:
                            throw new Exception("Unable to decompress encoding " + Format);
                    }
                    Decompress(subImage, format);
                }
            }
        }

        public void Decompress(SubImage subImage, DXT.Format format)
        {
            if ((format != DXT.Format.DXT1) &&
                (format != DXT.Format.DXT3) &&
                (format != DXT.Format.DXT5))
            {
                throw new Exception(string.Format("Unknown compressed format {0}", format));
            }

            /* The DXT algorithms work on 4x4 pixel blocks. Textures smaller than one
        	 * block will be padded, but larger textures need to be correctly aligned. */

            SubImage tmp = new SubImage();
            tmp.Width        = subImage.Width;
            tmp.Height       = subImage.Height;
            tmp.CanvasWidth  = subImage.CanvasWidth;
            tmp.CanvasHeight = subImage.CanvasHeight;
            tmp.DataSize     = (UInt32)(Math.Max(tmp.CanvasWidth * tmp.CanvasHeight * 4, 64));
            tmp.Data         = new byte[tmp.DataSize];

            BinaryReader reader = new BinaryReader(new MemoryStream(subImage.Data));
            switch (format)
            {
                case DXT.Format.DXT1:
                    DXT.DecompressDXT1(tmp.Data, reader, tmp.Width, tmp.Height, (UInt32)(tmp.Width * 4));
                    break;
                case DXT.Format.DXT3:
                    DXT.DecompressDXT3(tmp.Data, reader, tmp.Width, tmp.Height, (UInt32)(tmp.Width * 4));
                    break;
                case DXT.Format.DXT5:
                    DXT.DecompressDXT5(tmp.Data, reader, tmp.Width, tmp.Height, (UInt32)(tmp.Width * 4));
                    break;
            }
            reader.Close();
            subImage.DataSize = tmp.DataSize;
            subImage.Data = tmp.Data;
        }
        #endregion ReadTPC

        #region CreateTPC
        public void AddImage (Bitmap bitmap)
        {
            Image image = new Image();

            if (ImageCount == 0)
            {
                // This is the first image, extract the Width and Height from it
                Width = (UInt16)bitmap.Width;
                Height = (UInt16)bitmap.Height;
            }

            UInt16 w = Width;
            UInt16 h = Height;
            for (int subImageIndex = 0; subImageIndex < SubImageCount; subImageIndex++)
            {
                SubImage subImage = new SubImage();
                subImage.Width = w;
                subImage.Height = h;
                subImage.CanvasWidth = (UInt16)(Math.Ceiling((float)Math.Max(subImage.Width, (UInt16)1) / Quantization) * Quantization);
                subImage.CanvasHeight = (UInt16)(Math.Ceiling((float)Math.Max(subImage.Height, (UInt16)1) / Quantization) * Quantization);
                subImage.DataSize = (UInt32)Math.Ceiling(subImage.CanvasWidth * subImage.CanvasHeight * BytesPerPixel);
                subImage.Data = new byte[subImage.DataSize];

                // Convert pixel format
                if (isCompressed)
                {
                    // Compress DXT
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color c = bitmap.GetPixel(x, y);
                            switch (Format)
                            {
                                case EncodingFormat.Gray:
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel)] = (byte)((c.R + c.G + c.B) / 3);
                                    break;
                                case EncodingFormat.RGB:
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 0] = c.R;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 1] = c.G;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 2] = c.B;
                                    break;
                                case EncodingFormat.RGBA:
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 0] = c.R;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 1] = c.G;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 2] = c.B;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 3] = c.A;
                                    break;
                                case EncodingFormat.BGRA:
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 0] = c.B;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 1] = c.G;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 2] = c.R;
                                    subImage.Data[(int)(((h - y - 1) * w + x) * BytesPerPixel) + 3] = c.A;
                                    break;
                            }
                        }
                    }
                }

                // Add to pixeldata
                // TODO: This is not very memory or speed efficient, freeing up and re-constructing byte arrays like a mad man.
                int offset;
                if (PixelData == null)
                {
                    offset = 0;
                    PixelData = new byte[subImage.DataSize];
                }
                else
                {
                    offset = PixelData.GetLength(0);
                    byte[] buf = new byte[offset + subImage.DataSize];
                    for (int i = 0; i < offset; i++)
                    {
                        buf[i] = PixelData[i];
                    }
                    PixelData = buf;
                }
                for (int i = 0; i < subImage.DataSize; i++)
                {
                    PixelData[offset + i] = subImage.Data[i];
                }

                // Prepare for next sub image
                w >>= 1;
                h >>= 1;
                bitmap = ScaleBitmap(bitmap, w, h, InterpolationMode.HighQualityBicubic);
            }
        }

        public Bitmap ScaleBitmap (System.Drawing.Image image, UInt16 width, UInt16 height, InterpolationMode interpolationMode)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = interpolationMode;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        public void Save(Stream outStream)
        {
            BinaryWriter writer = new BinaryWriter(outStream);

            // Save header
            writer.Write(isCompressed ? BaseImageDataSize : (UInt32)0);
            writer.Write(Unknown1);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((byte)Format);
            writer.Write(SubImageCount);
            for (int i = 0; i < 114; i++)
            {
                writer.Write((byte)0);
            }

            // Save pixel data
            writer.Write(PixelData);

            // Save TXI
            TXI.Save(writer);
        }
        #endregion CreateTPC

        public static void Debug(string msg)
        {
            if (DEBUG)
            {
                Console.WriteLine(msg);
            }
        }
    }

}
