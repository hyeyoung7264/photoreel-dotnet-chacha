using System.Globalization;
namespace Photoreel;

public sealed class PlanRequest
{
    public string Concept { get; set; } = ""; public string Title { get; set; } = ""; public string Aspect { get; set; } = "9:16";
    public double? TargetDuration { get; set; }
    public bool Music { get; set; } = true; public string? Style { get; set; }
    public bool KeepCurrentOrder { get; set; }
}
public static class Planner
{
    static readonly (string Key, string Name, string[] Members)[] Groups = [
        ("scenery","풍경",["aerial","nature","sea","road","plant"]),("people","인물",["person","people"]),("animal","동물",["animal"]),
        ("town","거리·장소",["street","market","sign","vehicle","indoor","object"]),("food","음식",["food"]),("evening","해질녘·밤",["sunset","night"])];
    static readonly string[] Outdoor = ["aerial", "nature", "sea", "road", "street", "sunset", "night"];
    static string Group(Photo p)
    {
        if (p.Tags.Light is "dusk" or "night" && (p.Tags.Scene is null || Outdoor.Contains(p.Tags.Scene))) return "evening";
        if (p.FaceArea >= .01 && p.Tags.Scene is not "food" and not "animal") return "people";
        foreach (var g in Groups) if (g.Members.Contains(p.Tags.Scene)) return g.Key;
        return p.Tags.Scene is null ? "scenery" : "town";
    }
    static string SceneName(Photo p) => VisionModels.Scenes.FirstOrDefault(l => l.Key == p.Tags.Scene)?.Name ?? "사진";
    static DateTime? Time(Photo p) => DateTime.TryParse(p.TakenAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    public static Dictionary<string, string> Duplicates(List<Photo> photos, Dictionary<string, float[]> embeddings)
    {
        var drop = new Dictionary<string, string>();
        for (int i = 0; i < photos.Count; i++) foreach (var b in photos.Skip(i + 1))
        {
            var a = photos[i]; if (drop.ContainsKey(a.Id) || drop.ContainsKey(b.Id)) continue;
            double threshold = a.Methods.GetValueOrDefault("semantic") switch { "clip" => .90, "color-hist" => .985, _ => .95 };
            if (VisionModels.Dot(embeddings[a.Id], embeddings[b.Id]) < threshold) continue;
            var (keep, lose) = a.Sharpness >= b.Sharpness ? (a, b) : (b, a); drop[lose.Id] = keep.Id;
        }
        return drop;
    }
    static List<string> Chain(List<string> ids, Dictionary<string, float[]> embs, string? start)
    {
        var rest = ids.ToList(); var output = new List<string>(); var current = start;
        while (rest.Count > 0) { var next = current is null ? rest[0] : rest.MaxBy(id => VisionModels.Dot(embs[current], embs[id]))!; rest.Remove(next); output.Add(next); current = next; }
        return output;
    }
    static double OpenerScore(Photo p)
    {
        var top = p.Tags.SceneTop.ToDictionary(x => x[0].ToString()!, x => double.Parse(x[1].ToString()!, CultureInfo.InvariantCulture));
        return 1.5 * top.GetValueOrDefault("aerial") + top.GetValueOrDefault("nature") + top.GetValueOrDefault("sea") + .6 * top.GetValueOrDefault("street") + (p.Tags.Scale == "wide" ? .5 : 0) + .4 * p.Colorfulness + .3 * p.Brightness - 3 * p.FaceArea;
    }
    static (List<string> Order, Dictionary<string, int> Chapter, Dictionary<string, string> Reason) ByContent(List<Photo> photos, Dictionary<string, float[]> embs)
    {
        var byId = photos.ToDictionary(p => p.Id); var groups = photos.GroupBy(Group).ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToList());
        var day = photos.Where(p => Group(p) != "evening").ToList(); var opener = (day.Count > 0 ? day : photos).MaxBy(OpenerScore)!.Id; var first = Group(byId[opener]);
        var sequence = new List<string> { first }; sequence.AddRange(Groups.Select(g => g.Key).Where(k => k != first && k != "evening")); if (first != "evening") sequence.Add("evening");
        var order = new List<string>(); var chapter = new Dictionary<string, int>(); var reason = new Dictionary<string, string>(); int no = 0;
        foreach (var group in sequence)
        {
            if (!groups.TryGetValue(group, out var ids)) continue; List<string> chained;
            if (group == "evening")
            {
                var dusk = ids.Where(id => byId[id].Tags.Light != "night").ToList(); var night = ids.Where(id => byId[id].Tags.Light == "night").ToList();
                chained = Chain(dusk, embs, order.LastOrDefault()); chained.AddRange(Chain(night, embs, dusk.LastOrDefault()));
            }
            else if (group == first && ids.Contains(opener)) { chained = [opener]; chained.AddRange(Chain(ids.Where(id => id != opener).ToList(), embs, opener)); }
            else chained = Chain(ids, embs, order.LastOrDefault());
            foreach (var id in chained) { chapter[id] = no; reason[id] = group == "evening" ? $"{VisionModels.Lights.FirstOrDefault(l => l.Key == byId[id].Tags.Light)?.Name ?? "어두운"} 빛의 사진이라 끝부분에 배치" : $"‘{Groups.First(g => g.Key == group).Name}’ 묶음 · {SceneName(byId[id])} 사진으로 분류"; }
            order.AddRange(chained); no++;
        }
        reason[opener] = SceneName(byId[opener]) + " 사진으로 분류 · 넓게 보이는 장면이라 시작에 배치"; return (order, chapter, reason);
    }
    static (List<string> Order, Dictionary<string, int> Chapter, Dictionary<string, string> Reason) ByTime(List<Photo> photos)
    {
        var times = new Dictionary<string, DateTime>(); DateTime? last = null;
        foreach (var p in photos) { var t = Time(p); if (t.HasValue) last = t; times[p.Id] = t ?? last ?? DateTime.MinValue; }
        var order = photos.Select((p, i) => (p, i)).OrderBy(x => times[x.p.Id]).ThenBy(x => x.i).Select(x => x.p.Id).ToList(); var byId = photos.ToDictionary(p => p.Id);
        var chapter = new Dictionary<string, int>(); var reason = new Dictionary<string, string>(); int no = 0; DateTime? prev = null;
        foreach (var id in order) { var t = Time(byId[id]); if (t.HasValue && prev.HasValue && ((t.Value - prev.Value).TotalMinutes > 45 || t.Value.Date != prev.Value.Date)) no++; chapter[id] = no; reason[id] = t.HasValue ? $"촬영 시각 순 ({t:MM-dd HH:mm})" : "촬영 시각이 없어 올린 순서상 앞 사진 뒤에 둠"; prev = t ?? prev; }
        return (order, chapter, reason);
    }
    public static (int Beats, int MaxScenes, string Note) FitBeats(int n, Style style, double pace, double? target, int extra)
    {
        double lo = target.HasValue ? target.Value * .92 : 15 * pace, hi = target.HasValue ? target.Value * 1.08 : 30 * pace;
        int beats = Math.Clamp((int)Math.Round(style.Beats * pace), style.MinBeats, style.MaxBeats);
        double Total(int b, int count) => (b * count + extra) * style.BeatSec;
        while (beats > style.MinBeats && Total(beats, n) > hi) beats--; while (beats < style.MaxBeats && Total(beats + 1, n) <= hi && Total(beats, n) < lo) beats++;
        int max = n; while (max > 1 && Total(beats, max) > hi) max--;
        return (beats, max, Total(beats, max) < lo ? $"사진 수가 적어 영상이 {(target.HasValue ? "목표" : "기본 범위(15~30초)")}보다 짧습니다." : "");
    }
    public static Storyboard Build(List<Photo> photos, Dictionary<string, float[]> embs, PlanRequest req, Storyboard? current = null)
    {
        if (req.Concept is null || req.Title is null || req.Aspect is null) throw new ArgumentException("콘셉트, 제목, 화면 비율에 null을 사용할 수 없습니다.");
        if (photos.Count == 0) throw new ArgumentException("사진을 먼저 올려 주세요.");
        if (!Catalog.Aspects.ContainsKey(req.Aspect)) throw new ArgumentException("지원하지 않는 화면 비율입니다.");
        if (req.Style is not null && !Catalog.Styles.ContainsKey(req.Style)) throw new ArgumentException("알 수 없는 스타일입니다.");
        if (req.TargetDuration.HasValue && (!double.IsFinite(req.TargetDuration.Value) || req.TargetDuration is < 5 or > 180)) throw new ArgumentException("길이는 5~180초 사이로 정해 주세요.");
        var concept = req.Concept[..Math.Min(500, req.Concept.Length)]; var title = req.Title[..Math.Min(60, req.Title.Length)]; var reading = Concept.Interpret(concept); var style = Catalog.Styles[req.Style ?? reading.Style];
        double? target = req.TargetDuration ?? reading.Duration; bool music = req.Music && reading.Music != false; var size = Catalog.Aspects[req.Aspect]; double ratio = (double)size[0] / size[1]; var byId = photos.ToDictionary(p => p.Id);
        var notes = new List<string>(); var excluded = new Dictionary<string, string>(); List<string> order; Dictionary<string, int> chapter; Dictionary<string, string> reason; string mode, basis;
        bool manual = req.KeepCurrentOrder && current is not null;
        if (manual)
        {
            var known = current!.Scenes.Where(s => byId.ContainsKey(s.PhotoId)).ToList(); order = known.Where(s => s.Included).Select(s => s.PhotoId).ToList(); excluded = known.Where(s => !s.Included).ToDictionary(s => s.PhotoId, s => string.IsNullOrEmpty(s.ExcludeReason) ? "직접 제외함" : s.ExcludeReason);
            order.AddRange(photos.Select(p => p.Id).Where(id => !order.Contains(id) && !excluded.ContainsKey(id))); chapter = order.ToDictionary(id => id, _ => 0); reason = order.ToDictionary(id => id, _ => "직접 정한 순서"); mode = "manual"; basis = "직접 정한 순서를 유지했습니다.";
        }
        else
        {
            foreach (var (lose, keep) in Duplicates(photos, embs)) excluded[lose] = $"{byId[keep].Filename}와(과) 거의 같은 사진이라 제외 (더 선명한 쪽을 남김)";
            var candidates = photos.Where(p => !excluded.ContainsKey(p.Id)).ToList(); int timed = candidates.Count(p => !string.IsNullOrEmpty(p.TakenAt)); bool span = timed >= Math.Max(2, .8 * candidates.Count);
            if (reading.Order == "upload") { order = candidates.Select(p => p.Id).ToList(); chapter = order.ToDictionary(id => id, _ => 0); reason = order.ToDictionary(id => id, _ => "올린 순서 그대로"); mode = "upload"; basis = "요청에 따라 올린 순서를 그대로 썼습니다."; }
            else if (span) { (order, chapter, reason) = ByTime(candidates); mode = "chronological"; basis = $"사진에 기록된 촬영 시각 순으로 배열했습니다 ({timed}/{candidates.Count}장에 시각 정보 있음)."; }
            else { (order, chapter, reason) = ByContent(candidates, embs); mode = "content"; basis = $"촬영 시각 정보가 부족해({timed}/{candidates.Count}장) 사진에 보이는 내용과 빛 상태로 배열했습니다. 실제 촬영 순서와 다를 수 있습니다."; if (reading.Order == "chronological") notes.Add("시간순을 요청했지만 사진에 촬영 시각 정보가 부족해 내용 기준으로 배열했습니다."); }
        }
        var (beats, maxScenes, shortNote) = FitBeats(order.Count, style, reading.Pace, target, 3 + (title.Length > 0 ? 1 : 0)); if (shortNote.Length > 0) notes.Add(shortNote);
        if (!manual && order.Count > maxScenes)
        {
            var protect = order.Where(id => reading.Emphasis.Contains(byId[id].Tags.Scene ?? "")).ToHashSet(); protect.Add(order[0]);
            while (order.Count > maxScenes) { var candidates = order.Where(id => !protect.Contains(id)).ToList(); if (candidates.Count == 0) candidates = order.ToList(); var worst = candidates.MaxBy(id => order.Where(j => j != id).Select(j => VisionModels.Dot(embs[id], embs[j])).DefaultIfEmpty(0).Max())!; order.Remove(worst); excluded[worst] = "길이에 맞추려고 제외 (비슷한 장면이 더 있음)"; }
        }
        var scenes = new List<Scene>(); bool zoom = false, direction = false; string lastMotion = ""; int sinceAccent = 0;
        for (int index = 0; index < order.Count; index++)
        {
            string id = order[index]; var p = byId[id]; var (framing, framingReason) = Camera.ChooseFraming(p, ratio); string motion;
            if (framing == "fit-blur") { zoom = !zoom; motion = zoom ? "zoom-in" : "zoom-out"; }
            else
            {
                var (ww, wh) = Camera.CoverWindow(p.Width, p.Height, ratio); var (_, _, mass) = Camera.BestCenter(p.Saliency, ww, wh);
                if (index == order.Count - 1 && order.Count > 1) motion = "zoom-out";
                else if (p.Faces.Count > 0) motion = "zoom-in";
                else if ((ww < .8 || wh < .8) && (mass < .5 || lastMotion != "pan")) { lastMotion = "pan"; direction = !direction; motion = ww < .8 ? (direction ? "pan-right" : "pan-left") : (direction ? "pan-up" : "pan-down"); }
                else { lastMotion = "zoom"; if (index == 0) motion = "zoom-in"; else { zoom = !zoom; motion = zoom ? "zoom-in" : "zoom-out"; } }
            }
            int b = beats; if (index == 0) b += 1 + (title.Length > 0 ? 1 : 0); if (index == order.Count - 1 && order.Count > 1) b += 2; if (reading.Emphasis.Contains(p.Tags.Scene ?? "") && index > 0 && index < order.Count - 1) b++;
            var path = Camera.PlanPath(p.Width, p.Height, ratio, motion, framing, b * style.BeatSec, style.ZoomRate, style.PanRate, p.Focus, p.Saliency, p.Faces); sinceAccent++;
            string transition = index == 0 ? "cut" : chapter.GetValueOrDefault(id) != chapter.GetValueOrDefault(order[index - 1]) && sinceAccent >= 3 ? style.ChapterTransition : style.Transition;
            if (index > 0 && chapter.GetValueOrDefault(id) != chapter.GetValueOrDefault(order[index - 1]) && sinceAccent >= 3) sinceAccent = 0;
            var why = reason.GetValueOrDefault(id, ""); if (index == order.Count - 1 && order.Count > 1) why += " · 마지막 장면이라 길게 보여 주고 어둡게 마무리";
            scenes.Add(new() { PhotoId = id, Beats = b, Motion = path.Motion, Framing = framing, Transition = transition, Chapter = chapter.GetValueOrDefault(id), Reason = why, FramingReason = framingReason });
        }
        foreach (var (id, why) in excluded) scenes.Add(new() { PhotoId = id, Included = false, Beats = beats, ExcludeReason = why, Transition = style.Transition });
        notes.Add(music ? "음악은 앱이 직접 합성한 음원입니다 (외부 음원 미사용). 장면 전환을 박자에 맞췄습니다." : "음악 없이 만듭니다.");
        if (photos.Any(p => p.Methods.GetValueOrDefault("semantic") == "color-hist")) notes.Add("사진 내용 분류 모델을 쓰지 못해 색 분포만으로 비교했습니다. 순서와 중복 판단이 부정확할 수 있습니다.");
        return new Storyboard { Concept = concept, Title = title.Trim(), Style = style.Id, StyleReason = req.Style is not null ? "직접 고른 스타일" : reading.StyleMatched ? "콘셉트에서 분위기를 읽어 고름" : "콘셉트에서 분위기 단서를 찾지 못해 기본 스타일을 씀", Grade = reading.Grade ?? style.Grade, Output = new() { Aspect = req.Aspect, Width = size[0], Height = size[1] }, Music = new() { Mode = music ? "synth" : "none", Mood = style.MusicMood, Bpm = style.Bpm }, OrderMode = mode, OrderBasis = basis, Signals = reading.Signals, Unsupported = reading.Unsupported, Notes = notes, Scenes = scenes, TargetDuration = target, Engine = manual && Engines.Available(current!.Engine) ? current.Engine : "edit" }.FinalizeBoard();
    }
}
