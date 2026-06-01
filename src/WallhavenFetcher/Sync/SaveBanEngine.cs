namespace WallhavenFetcher.Sync;

/// <summary>
/// Save / ban / unsave / unban operations. All take canonical IDs and
/// operate on State + the wallpaper folder + favorites/. Verify-then-delete
/// pattern: only unlink files we confirmed exist (no silent no-ops).
/// </summary>
public sealed class SaveBanEngine
{
    private readonly Action<string> _log;
    public SaveBanEngine(Action<string> log) => _log = log;

    public SaveResult Save(IEnumerable<CanonicalId> cids, State state, string folder)
    {
        var result = new SaveResult();
        foreach (var cid in cids)
        {
            // Find the file in the main folder
            var match = Directory.EnumerateFiles(folder, cid.FileGlob()).FirstOrDefault();
            if (match is null)
            {
                result.Missing.Add(cid);
                continue;
            }
            if (state.IsSaved(cid))
            {
                // Backfill favorite copy if it's missing
                if (FavoritesManager.Ensure(cid, match, folder))
                    result.FavoritesAdded.Add(cid);
                result.AlreadySaved.Add(cid);
                continue;
            }
            state.AddSaved(cid);
            result.NewlySaved.Add(cid);
            if (FavoritesManager.Ensure(cid, match, folder))
                result.FavoritesAdded.Add(cid);
            _log($"Saved {cid}");
        }
        state.Save(Paths.StateFile);
        return result;
    }

    public BanResult Ban(IEnumerable<CanonicalId> cids, State state, string folder)
    {
        var result = new BanResult();
        var bannedSet = state.Banned.ToHashSet();
        var savedSet = state.Saved.ToHashSet();

        foreach (var cid in cids)
        {
            // Always sweep favorites — even for already-banned IDs (leftover
            // favorite from earlier save needs to go).
            foreach (var name in FavoritesManager.Remove(cid, folder))
                result.FavoritesDeleted.Add(name);

            var s = cid.ToString();
            if (bannedSet.Contains(s))
            {
                result.AlreadyBanned.Add(cid);
                continue;
            }
            if (savedSet.Contains(s))
            {
                state.RemoveSaved(cid);
                savedSet.Remove(s);
                result.UnsavedFirst.Add(cid);
            }
            state.AddBanned(cid);
            bannedSet.Add(s);
            result.NewlyBanned.Add(cid);

            // Verify-then-delete main file
            var match = Directory.EnumerateFiles(folder, cid.FileGlob()).FirstOrDefault();
            if (match is not null && File.Exists(match))
            {
                try
                {
                    File.Delete(match);
                    result.MainDeleted.Add(Path.GetFileName(match));
                    state.Of(cid.Source).Images.Remove(cid.ItemId);
                }
                catch (Exception ex)
                {
                    _log($"Could not delete {match}: {ex.Message}");
                }
            }
            else if (state.Of(cid.Source).Images.ContainsKey(cid.ItemId))
            {
                // State knew about it but disk didn't.
                result.GhostInState.Add(cid);
                state.Of(cid.Source).Images.Remove(cid.ItemId);
            }
            _log($"Banned {cid}");
        }
        state.Save(Paths.StateFile);
        return result;
    }

    public IReadOnlyList<CanonicalId> Unsave(IEnumerable<CanonicalId> cids, State state)
    {
        var removed = new List<CanonicalId>();
        foreach (var cid in cids)
        {
            if (state.IsSaved(cid))
            {
                state.RemoveSaved(cid);
                removed.Add(cid);
            }
        }
        state.Save(Paths.StateFile);
        return removed;
    }

    public IReadOnlyList<CanonicalId> Unban(IEnumerable<CanonicalId> cids, State state)
    {
        var removed = new List<CanonicalId>();
        foreach (var cid in cids)
        {
            var s = cid.ToString();
            if (state.Banned.Remove(s)) removed.Add(cid);
        }
        state.Save(Paths.StateFile);
        return removed;
    }
}

public sealed class SaveResult
{
    public List<CanonicalId> NewlySaved   { get; } = new();
    public List<CanonicalId> AlreadySaved { get; } = new();
    public List<CanonicalId> Missing      { get; } = new();
    public List<CanonicalId> FavoritesAdded { get; } = new();

    public bool Any() => NewlySaved.Count > 0 || AlreadySaved.Count > 0;
}

public sealed class BanResult
{
    public List<CanonicalId> NewlyBanned     { get; } = new();
    public List<CanonicalId> AlreadyBanned   { get; } = new();
    public List<CanonicalId> UnsavedFirst    { get; } = new();
    public List<string>      MainDeleted     { get; } = new();
    public List<string>      FavoritesDeleted{ get; } = new();
    public List<CanonicalId> GhostInState    { get; } = new();

    public bool Any() => NewlyBanned.Count > 0 || AlreadyBanned.Count > 0;
}
