using System.IO;
using System.Windows.Media.Imaging;

namespace ReolinkDBWin.Services;

public static class ImageHelper
{
    /// <summary>Décode un JPEG/PNG en BitmapImage gelée (utilisable depuis n'importe quel thread).</summary>
    public static BitmapImage? ToBitmap(byte[]? bytes, int decodeWidth = 0)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Écrit l'image dans le cache et retourne son chemin (les notifications Windows exigent un fichier).</summary>
    public static string? SaveToCache(byte[]? bytes, string name)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var path = Path.Combine(AppPaths.CacheDirectory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
