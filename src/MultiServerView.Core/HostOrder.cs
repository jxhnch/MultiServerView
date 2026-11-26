namespace MultiServerView.Core;

/// <summary>
/// The order of the host list, the same as in the "All" view of the app: sections (workspace and group) in the order the user
/// dragged them into, the others by name, hosts without both last; inside a section the places the user chose by drag and drop, then everything else by name.
/// </summary>
public static class HostOrder
{
    /// <summary>The name of a section in the saved order. The UI builds the same text (HostsPage.tsx, sectionKey).</summary>
    public static string SectionKey(string workspace, string group) => workspace + "\u001f" + group;

    public static List<ServerEntry> Sort(IEnumerable<ServerEntry> hosts, IReadOnlyList<string>? groupOrder = null)
    {
        var places = new Dictionary<string, int>();
        for (var i = 0; i < (groupOrder?.Count ?? 0); i++) places.TryAdd(groupOrder![i], i);
        return hosts
        .OrderBy(s => s.Workspace.Length == 0 && s.Group.Length == 0 ? 1 : 0)
        .ThenBy(s => places.TryGetValue(SectionKey(s.Workspace, s.Group), out var place) ? place : int.MaxValue)
        .ThenBy(s => s.Workspace, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(s => s.SortOrder > 0 ? s.SortOrder : int.MaxValue)
        .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
    }
}
