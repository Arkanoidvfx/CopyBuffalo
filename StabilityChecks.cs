using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

static class StabilityChecks
{
    internal static void Run(string dir, Action<bool, string> check)
    {
        var root = Path.Combine(dir, "stability");
        var store = new HistoryStore(Path.Combine(root, "history.dat"));
        byte[] bytes = "ORIGINAL"u8.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        ImageFiles.WriteMedia(store.ImagesDir, hash, bytes, null);
        store.AddMedia(hash, "text", "example.txt", bytes.Length, false, DateTime.UtcNow);
        var first = Paster.PrepareFiles(store.Items[0], store.ImagesDir).paths[0];
        File.WriteAllText(first, "MODIFIED");
        var second = Paster.PrepareFiles(store.Items[0], store.ImagesDir).paths[0];
        check(first != second && File.ReadAllBytes(second).SequenceEqual(bytes), "stability: repeated paste uses original bytes after same-size external edit");
        var other = Path.Combine(root, "other.txt"); File.WriteAllText(other, "OTHER");
        var bundle = FileBundle.Pack([second, other], 1000000, 2);
        var extracted = FileBundle.Extract(bundle, Path.Combine(root, "bundle"), 2);
        File.WriteAllText(extracted[0], "MODIFIED");
        var repeated = FileBundle.Extract(bundle, Path.Combine(root, "bundle"), 2);
        check(File.ReadAllBytes(repeated[0]).SequenceEqual(bytes), "stability: bundle extraction never reuses externally edited files");
        store.Save();
        File.WriteAllText(Path.Combine(root, "history.dat"), "synthetic corruption");
        new HistoryStore(Path.Combine(root, "history.dat")).Load(DateTime.UtcNow);
        var backup = Directory.GetDirectories(root, "history.corrupt-*.dat.images").Single();
        check(ImageFiles.ReadFull(backup, hash).SequenceEqual(bytes), "stability: corrupt index preserves non-image attachments in quarantine");
        new HistoryStore(Path.Combine(root, "history.dat")).Load(DateTime.UtcNow);
        check(File.Exists(Path.Combine(backup, hash + ".dat")), "stability: next startup retains quarantined attachments");

        byte[] pixels = [0, 0, 255, 255, 0, 0, 255, 255];
        var wide = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var tall = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var a = ImageFiles.Normalize(wide); var b = ImageFiles.Normalize(tall);
        check(a.hash != b.hash, "stability: image dimensions distinguish otherwise identical pixel streams");
        var images = new HistoryStore(Path.Combine(root, "captures", "history.dat"));
        ImageFiles.Write(images.ImagesDir, a.hash, a.bgra);
        images.AddImage(a.hash, 2, 1, DateTime.UtcNow);
        using (images.PreserveBlobFiles())
        {
            images.Remove(images.Items[0]);
            check(File.Exists(Path.Combine(images.ImagesDir, a.hash + ".dat")), "stability: deletion cannot remove a pending capture payload");
            images.AddImage(a.hash, 2, 1, DateTime.UtcNow);
        }
        check(ImageFiles.ReadFull(images.ImagesDir, a.hash).Length > 0, "stability: recaptured record retains its payload after deferred deletion");
        using (images.PreserveBlobFiles()) images.Remove(images.Items[0]);
        check(!File.Exists(Path.Combine(images.ImagesDir, a.hash + ".dat")), "stability: genuinely deleted payload is cleaned after writers finish");
    }
}
