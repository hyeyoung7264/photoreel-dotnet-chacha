using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Photoreel;

public sealed class Project
{
    static readonly ConcurrentDictionary<string, object> Locks = new();
    public static readonly HashSet<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff"];
    public string Id { get; }
    public string DirectoryPath { get; }
    public object Gate { get; }
    public Project(string id)
    {
        if (!Regex.IsMatch(id, "^[0-9a-f]{6,32}$")) throw new ArgumentException("잘못된 프로젝트 id");
        Id = id; DirectoryPath = Path.Combine(Settings.Data, "projects", id); Gate = Locks.GetOrAdd(DirectoryPath, _ => new());
    }
    public static Project Create()
    {
        var p = new Project(Guid.NewGuid().ToString("N")[..12]);
        foreach (var sub in new[] { "orig", "proxy", "thumb", "emb", "renders" }) Directory.CreateDirectory(Path.Combine(p.DirectoryPath, sub));
        Settings.WriteJson(Path.Combine(p.DirectoryPath, "photos.json"), Array.Empty<Photo>()); return p;
    }
    public bool Exists => File.Exists(Path.Combine(DirectoryPath, "photos.json"));
    public List<Photo> Photos() => Settings.ReadJson<List<Photo>>(Path.Combine(DirectoryPath, "photos.json")) ?? [];
    public Storyboard? Plan() => Settings.ReadJson<Storyboard>(Path.Combine(DirectoryPath, "plan.json"));
    public void SavePlan(Storyboard board) { lock (Gate) Settings.WriteJson(Path.Combine(DirectoryPath, "plan.json"), board); }
    public string Proxy(string id) => Path.Combine(DirectoryPath, "proxy", id + ".jpg");
    public string Thumb(string id) => Path.Combine(DirectoryPath, "thumb", id + ".jpg");
    public Photo AddPhoto(string filename, byte[] bytes, VisionModels models)
    {
        lock (Gate)
        {
            if (Photos().Count >= 80) throw new ArgumentException("사진은 최대 80장까지입니다.");
            var ext = Path.GetExtension(filename).ToLowerInvariant(); if (!ImageExtensions.Contains(ext)) throw new ArgumentException("지원하지 않는 파일 형식: " + filename);
            if (bytes.Length > 60 * 1024 * 1024) throw new ArgumentException("파일이 너무 큽니다 (60MB 초과).");
            var id = Guid.NewGuid().ToString("N")[..10]; var original = Path.Combine(DirectoryPath, "orig", id + ext); File.WriteAllBytes(original, bytes);
            try
            {
                var (photo, embedding) = Analyzer.Analyze(id, original, Proxy(id), Path.GetFileName(filename), models);
                using var proxy = Images.Load(Proxy(id)); using var thumbnail = Images.Resize(proxy, 480); Images.SaveJpeg(thumbnail, Thumb(id), 85);
                Npy.Write(Path.Combine(DirectoryPath, "emb", id + ".npy"), embedding, [embedding.Length]);
                var photos = Photos(); photos.Add(photo); Settings.WriteJson(Path.Combine(DirectoryPath, "photos.json"), photos); return photo;
            }
            catch (Exception e) { foreach (var path in new[] { original, Proxy(id), Thumb(id), Path.Combine(DirectoryPath, "emb", id + ".npy") }) if (File.Exists(path)) File.Delete(path); throw new ArgumentException($"사진을 읽지 못함: {filename} ({e.Message})", e); }
        }
    }
    public void RemovePhoto(string id)
    {
        lock (Gate)
        {
            var photos = Photos(); if (!photos.Any(p => p.Id == id)) throw new FileNotFoundException("사진을 찾을 수 없습니다."); photos.RemoveAll(p => p.Id == id); Settings.WriteJson(Path.Combine(DirectoryPath, "photos.json"), photos);
            foreach (var sub in new[] { "orig", "proxy", "thumb", "emb", "depth" }) { var root = Path.Combine(DirectoryPath, sub); if (Directory.Exists(root)) foreach (var file in Directory.EnumerateFiles(root, id + ".*")) File.Delete(file); }
            var board = Plan(); if (board is not null) { board.Scenes.RemoveAll(s => s.PhotoId == id); SavePlan(board.FinalizeBoard()); }
        }
    }
    public Dictionary<string, float[]> Embeddings() => Photos().ToDictionary(p => p.Id, p => Npy.Read(Path.Combine(DirectoryPath, "emb", p.Id + ".npy")).Values);
    public void ValidatePlan(Storyboard board)
    {
        board.FinalizeBoard();
        var ids = Photos().Select(p => p.Id).ToHashSet(); var seen = new HashSet<string>();
        foreach (var s in board.Scenes) if (!ids.Contains(s.PhotoId) || !seen.Add(s.PhotoId)) throw new ArgumentException("구성안에 없는 사진이거나 같은 사진이 두 번 들어 있습니다.");
        if (!Engines.Ids.Contains(board.Engine)) throw new ArgumentException("알 수 없는 생성 방식입니다."); board.FinalizeBoard();
    }
    public (int Number, string Path) NewRender()
    {
        lock (Gate) { var root = Path.Combine(DirectoryPath, "renders"); Directory.CreateDirectory(root); int n = Directory.GetDirectories(root).Select(Path.GetFileName).Where(x => int.TryParse(x, out _)).Select(x => int.Parse(x!)).DefaultIfEmpty(0).Max() + 1; var path = RenderDirectory(n); Directory.CreateDirectory(path); return (n, path); }
    }
    public string RenderDirectory(int n) { if (n < 1) throw new ArgumentException("잘못된 render 번호"); return Path.Combine(DirectoryPath, "renders", n.ToString()); }
    public List<JsonElement> Renders()
    {
        var root = Path.Combine(DirectoryPath, "renders"); if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root).Where(x => int.TryParse(Path.GetFileName(x), out _)).OrderBy(x => int.Parse(Path.GetFileName(x))).Select(x => Path.Combine(x, "stats.json")).Where(File.Exists).Select(x => Settings.ReadJson<JsonElement>(x)).ToList();
    }
    public Dictionary<string, object?> SaveRender(int n, Storyboard board, Dictionary<string, object?> stats)
    {
        Settings.WriteJson(Path.Combine(RenderDirectory(n), "storyboard.json"), board);
        var result = new Dictionary<string, object?> { ["render"] = n, ["created_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") }; foreach (var (k, v) in stats) result[k] = v;
        Settings.WriteJson(Path.Combine(RenderDirectory(n), "stats.json"), result); return result;
    }
}
