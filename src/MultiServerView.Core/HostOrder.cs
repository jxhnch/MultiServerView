namespace MultiServerView.Core;

/// <summary>
/// The order of the host list, the same as in the "All" view of the app: by workspace and group (hosts without both come last),
/// inside a section the places the user chose by drag and drop, then everything else by name.
/// </summary>
public static class HostOrder
{
    public static List<ServerEntry> Sort(IEnumerable<ServerEntry> hosts) => hosts
        .OrderBy(s => s.Workspace.Length == 0 && s.Group.Length == 0 ? 1 : 0)
        .ThenBy(s => s.Workspace, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(s => s.SortOrder > 0 ? s.SortOrder : int.MaxValue)
        .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}
