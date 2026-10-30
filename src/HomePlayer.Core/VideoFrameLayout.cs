namespace HomePlayer.Core;

public static class VideoFrameLayout
{
    public static bool TryGetPlane(CpuFrameLease frame, int index, int rowBytes, int rows, out VideoFramePlane plane)
    {
        plane = default;
        if (index < 0 || index >= frame.Planes.Count || rowBytes <= 0 || rows <= 0)
            return false;
        plane = frame.Planes[index];
        if (plane.Offset < 0 || plane.Stride < rowBytes)
            return false;
        var required = (long)plane.Offset + (long)(rows - 1) * plane.Stride + rowBytes;
        return required <= frame.Data.Length;
    }
}
