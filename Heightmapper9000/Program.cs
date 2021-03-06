using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using System;
using System.Linq;

namespace Heightmapper9000
{
    class Program
    {
        static void Main(string[] args)
        {
            // Alright! Lets find the game directory, and open it.
            GameLocations.TryGetGameFolder(GameRelease.SkyrimSE, out string path);
            Console.WriteLine("Found Skyrim at: " + path);
            var mod = SkyrimMod.CreateFromBinaryOverlay(path + "\\Data\\Skyrim.esm", SkyrimRelease.SkyrimSE); 
            
            // Worldspaces are 'worlds', like the overworld and a whole slew of special ones.
            // The first is Tamriel, so we can just take that one.
            var worldspace = mod.Worldspaces.First();

            // We are going to find the minimum and maximum coordinates, both in height and the grid position.
            // We set all of them to the absolute highest the variables can contain, to start out with
            int maxX, maxY, minX, minY;
            maxX = maxY = minX = minY = int.MaxValue;
            float minZ = float.MaxValue, maxZ = float.MaxValue;

            // We then loop through the worldspace's cells (which are divided into blocks, subblocks, subcells
            foreach(var block in worldspace.SubCells)
            {
                foreach(var subblock in block.Items)
                {
                    foreach(var cell in subblock.Items)
                    {
                        // Here, we check if the variables are set to the max value *or* if its larger/smaller than max.
                        if (cell.Grid.Point.X > maxX || maxX == int.MaxValue) maxX = cell.Grid.Point.X;
                        if (cell.Grid.Point.X < minX || minX == int.MaxValue) minX = cell.Grid.Point.X;
                        if (cell.Grid.Point.Y > maxY || maxY == int.MaxValue) maxY = cell.Grid.Point.Y;
                        if (cell.Grid.Point.Y < minY || minY == int.MaxValue) minY = cell.Grid.Point.Y;

                        // We try to find the cell's heightmap, and stick it into a variable named vertexheights
                        if(!cell.Landscape.VertexHeightMap.TryGet(out var vertexheights)) continue;

                        // We then do the same as above, but with the height of each part of the heightmap
                        float[,] land = ParseLandHeights(vertexheights);
                        foreach(float f in land)
                        {
                            if (f < minZ || minZ == float.MaxValue) minZ = f;
                            if (f > maxZ || maxZ == float.MaxValue) maxZ = f;
                        }
                    }
                }
            }

            // To make debugging easier, we print out the values we have found. Makes it easy to see if we screwed something up.
            Console.WriteLine("Max X: {0}, Min X: {1} , Max Y: {2}, Min Y: {3}, Min Z: {4}, Max Z: {5}",  maxX, minX, maxY, minY, minZ, maxZ);

            // Alright! So, grid coordinates can be, like we saw from the line above, negative.
            // When we try to find the width and height of the world, we strip the minus sign and add both together.
            int world_width_in_cells = Math.Abs(maxX) + Math.Abs(minX) + 1;
            int world_height_in_cells = Math.Abs(maxY) + Math.Abs(minY) + 1;
            int row_size = 32 * world_width_in_cells; // Each heightmap is 32*32 large.

            // It's time to create somewhere to stick our picture! This creates a buffer large enough to hold everything
            byte[] buffer = new byte[32 * world_width_in_cells * 32 * world_height_in_cells * sizeof(short)];

            // Time for nested foreach loop mayhem again, but this time you know what is up.
            foreach (var block in worldspace.SubCells)
            {
                foreach (var subblock in block.Items)
                {
                    foreach (var cell in subblock.Items)
                    {
                        // Grab the heightmap...
                        if (!cell.Landscape.VertexHeightMap.TryGet(out var vertexheights)) continue;
                        float[,] land = ParseLandHeights(vertexheights);

                        // Do the same thing we did when we figured out the width of the world, this time for the cell coordinate
                        int cell_x_normalized = cell.Grid.Point.X + Math.Abs(minX);
                        int cell_y_normalized = cell.Grid.Point.Y + Math.Abs(minY);

                        // Time to loop through the heightmap
                        for(int y = 0; y < 32; y++)
                        {
                            // Where in the buffer we wind up if we only take the y position
                            int row_offset_in_bytes = ( (cell_y_normalized * row_size * 32) + (y * row_size) ) * 2;
                            for(int x = 0; x < 32; x++)
                            {
                                // Where are we on that row?
                                int column_offset_in_bytes = ((cell_x_normalized * 32) + x)* 2;
                                // The result is where we are supposed to put our data
                                int index = column_offset_in_bytes + row_offset_in_bytes;
                                // Here, we figure out where in that 16 bit grayscale would be a good color for the position.
                                // We calculate that via adding the lowest position in the world, the highest,
                                // then seeing where that would be percentage wise
                                float percent = (land[y, x] + Math.Abs(minZ)) / (maxZ + Math.Abs(minZ));
                                ushort color = (ushort)(ushort.MaxValue * percent);
                                
                                BitConverter.GetBytes(color).CopyTo(buffer, index); // Copy the color into the buffer
                            }
                        }
                    }
                }
            }

            // It's time to make a picture! Here, we describe to ImageMagick how to read the buffer
            ImageMagick.MagickReadSettings mr = new ImageMagick.MagickReadSettings();
            mr.Format = ImageMagick.MagickFormat.Gray;
            mr.Width = 32 * world_width_in_cells;
            mr.Height = 32 * world_height_in_cells;
            mr.Depth = 16;

            // Then we describe how we want to *write* the picture, and write it.
            ImageMagick.MagickImage m = new ImageMagick.MagickImage(buffer, mr);
            m.Format = ImageMagick.MagickFormat.Png;
            m.Depth = 16;
            m.ColorType = ImageMagick.ColorType.Grayscale;

            // Change this to what you want :)
            m.Write(@"C:\debug\imagemagick.png");
        }

        // This function transforms a heightmap into numbers we can read.
        // https://en.uesp.net/wiki/Skyrim_Mod:Mod_File_Format/LAND is the definition of the format
        static float[,] ParseLandHeights(ReadOnlyMemorySlice<byte> input)
        {
            // The data is *stored* as 33x33
            float[,] returner = new float[33, 33];

            // First value is how far relative to the rest of the world we start out as.
            float world_offset = BitConverter.ToSingle(input);

            for(int y = 0; y < 33; y++)
            {
                float row_offset = 0f;
                float currentpos = (sbyte)input[y * 33 + 4];
                world_offset += currentpos; // The first slot in each row changes the global offset.
                for (int x = 0; x < 33; x++)
                {
                    currentpos = (sbyte)input[y * 33 + x + 4];
                    row_offset += currentpos;
                    returner[y, x] = world_offset + row_offset;
                }
            }

            return returner;
        }
    }
}