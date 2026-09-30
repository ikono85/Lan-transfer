namespace LanLink.Core.Sync;

/// <summary>
/// Décide quoi copier ou supprimer en comparant l'état local (L), distant (R) et le dernier état
/// synchronisé (B, la « base »). Fonction pure, sans accès disque ni réseau.
/// </summary>
public static class SyncPlanner
{
    /// <summary>Tolérance sur les dates de modification (systèmes de fichiers à 2 s de précision).</summary>
    public static readonly long TickTolerance = TimeSpan.TicksPerSecond * 2;

    public static bool Same(SyncEntry a, SyncEntry b) =>
        a.Size == b.Size && Math.Abs(a.Ticks - b.Ticks) <= TickTolerance;

    public static SyncPlan Plan(IReadOnlyDictionary<string, SyncEntry> local, IReadOnlyDictionary<string, SyncEntry> remote,
        IReadOnlyDictionary<string, SyncEntry> baseline)
    {
        var actions = new List<SyncAction>();
        var newBase = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);

        var paths = new HashSet<string>(local.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(remote.Keys);
        paths.UnionWith(baseline.Keys);

        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            baseline.TryGetValue(path, out var b);

            // Un fichier en cours d'écriture d'un côté est laissé tel quel pour cette fois.
            if ((l?.Busy ?? false) || (r?.Busy ?? false))
            {
                if (b is not null) newBase[path] = b;
                continue;
            }

            if (l is not null && r is not null)
            {
                if (Same(l, r))
                {
                    newBase[path] = l;
                    continue;
                }

                var localChanged = b is null || !Same(l, b);
                var remoteChanged = b is null || !Same(r, b);
                if (localChanged && !remoteChanged) actions.Add(new SyncAction(path, SyncActionKind.Push));
                else if (!localChanged && remoteChanged) actions.Add(new SyncAction(path, SyncActionKind.Pull));
                else
                {
                    // Modifié des deux côtés (ou sans historique) : le plus récent gagne, l'autre est sauvegardé.
                    var localWins = l.Ticks != r.Ticks ? l.Ticks > r.Ticks : l.Size >= r.Size;
                    actions.Add(new SyncAction(path, localWins ? SyncActionKind.Push : SyncActionKind.Pull, Conflict: localChanged && remoteChanged));
                }
            }
            else if (l is not null)
            {
                // Absent à distance
                if (b is null) actions.Add(new SyncAction(path, SyncActionKind.Push));                 // nouveau ici
                else if (Same(l, b)) actions.Add(new SyncAction(path, SyncActionKind.DeleteLocal));    // supprimé là-bas
                else actions.Add(new SyncAction(path, SyncActionKind.Push, Conflict: true));           // modifié ici, supprimé là-bas : on garde
            }
            else if (r is not null)
            {
                // Absent ici
                if (b is null) actions.Add(new SyncAction(path, SyncActionKind.Pull));
                else if (Same(r, b)) actions.Add(new SyncAction(path, SyncActionKind.DeleteRemote));
                else actions.Add(new SyncAction(path, SyncActionKind.Pull, Conflict: true));
            }
            // Absent partout : disparaît de la base.
        }

        return new SyncPlan(actions, newBase);
    }
}
