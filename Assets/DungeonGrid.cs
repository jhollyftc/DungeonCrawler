using UnityEngine;

namespace DungeonGen
{
    public enum CellType : byte
    {
        Empty,
        Room,
        Hallway,
        StairLower,
        StairUpper,
        Prison
    }

    /// <summary>
    /// Flat-array 3D grid. Index layout = x + z*W + y*W*D (y-major so a full
    /// floor is contiguous, which is nice for debugging and future jobs).
    /// Pure C# — no scene dependencies.
    /// </summary>
    public class Grid3D<T>
    {
        public readonly int Width, Height, Depth; // X, Y, Z
        readonly T[] data;

        public Grid3D(int width, int height, int depth)
        {
            Width = width; Height = height; Depth = depth;
            data = new T[width * height * depth];
        }

        public int Index(int x, int y, int z) => x + z * Width + y * Width * Depth;
        public int Index(Vector3Int p) => Index(p.x, p.y, p.z);

        public bool InBounds(Vector3Int p) =>
            p.x >= 0 && p.x < Width &&
            p.y >= 0 && p.y < Height &&
            p.z >= 0 && p.z < Depth;

        public T this[int x, int y, int z]
        {
            get => data[Index(x, y, z)];
            set => data[Index(x, y, z)] = value;
        }

        public T this[Vector3Int p]
        {
            get => data[Index(p)];
            set => data[Index(p)] = value;
        }

        public T this[int i]
        {
            get => data[i];
            set => data[i] = value;
        }

        public int Length => data.Length;

        public Vector3Int Position(int i)
        {
            int y = i / (Width * Depth);
            int rem = i - y * Width * Depth;
            int z = rem / Width;
            int x = rem - z * Width;
            return new Vector3Int(x, y, z);
        }
    }
}