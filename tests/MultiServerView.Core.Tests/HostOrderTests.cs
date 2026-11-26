using MultiServerView.Core;
using Xunit;

namespace MultiServerView.Core.Tests;

public class HostOrderTests
{
    [Fact]
    public void Sections_first_then_own_places_then_names()
    {
        ServerEntry H(string name, string group = "", int order = 0, string workspace = "") => new() { Name = name, Group = group, SortOrder = order, Workspace = workspace };
        var sorted = HostOrder.Sort(new[] { H("loose"), H("b", "Prod"), H("a", "Prod"), H("z", "Prod", 1), H("y", "Prod", 2), H("n", "Home"), H("w", "Prod", 0, "Acme") });
        Assert.Equal(new[] { "n", "z", "y", "a", "b", "w", "loose" }, sorted.Select(s => s.Name));
    }
}
