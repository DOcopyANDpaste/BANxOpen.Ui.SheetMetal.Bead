using NXOpen;
using NXOpen.BlockStyler;

namespace BANxOpen.Ui.SheetMetal.Bead;

/// <summary>Owns one tree's contents (ShtMetal, BeadOptions) and the mapping from its rows back to the domain
/// objects they were rendered from. Adapted from the same class in BANxOpen.Ui.MaterialAssignment, trimmed to
/// flat, single-check trees (no multi-select, no context menus).
///
/// Rows are keyed by <see cref="TaggedObject.Tag"/>, NOT by the <see cref="Node"/> instance:
/// <c>BlockStyler.Node</c> derives from <c>TaggedObject</c>, and nothing in that chain (TaggedObject ->
/// NXRemotableObject -> MarshalByRefObject) overrides Equals/GetHashCode — so Nodes compare by reference.
/// If NX ever hands a callback a fresh managed wrapper for an existing row, a <c>Dictionary&lt;Node, T&gt;</c>
/// would silently miss and the user's click would resolve to nothing. Tag is the stable underlying identity
/// and is a plain struct, so it hashes correctly either way.
///
/// This keeps the convention <see cref="BlockAccessor"/> runs on: a selection is never turned back into a
/// domain value by parsing what the block displays — it is mapped through what was last populated.</summary>
public sealed class TreeBinding<T> where T : class
{
    private readonly Tree _tree;
    private readonly Dictionary<Tag, T> _byNodeTag = new();

    // The nodes in insertion order, so the check state can be re-asserted across the whole tree without
    // walking NX's sibling chain again on every click.
    private readonly List<(Node Node, T Value)> _rows = new();

    public TreeBinding(Tree tree) => _tree = tree;

    public IReadOnlyList<(Node Node, T Value)> Rows => _rows;

    /// <summary>Clears the tree and repopulates it via <paramref name="populate"/>, with the whole rebuild
    /// frozen behind Redraw(false)/Redraw(true) so NX paints once instead of once per node. Redraw is
    /// restored in a finally — leaving a tree frozen after an exception makes the dialog look hung.</summary>
    public void Rebuild(Action populate)
    {
        _tree.Redraw(false);
        try
        {
            Clear();
            populate();
        }
        finally
        {
            _tree.Redraw(true);
        }
    }

    /// <summary>Appends a row.</summary>
    public Node Add(string displayText, T value)
    {
        var node = _tree.CreateNode(displayText);

        // AlwaysLast rather than Sort: the row order is the standards file's own, and letting the tree
        // re-sort would silently override it.
        _tree.InsertNode(node, null, null, Tree.NodeInsertOption.AlwaysLast);

        _byNodeTag[node.Tag] = value;
        _rows.Add((node, value));
        return node;
    }

    public T? Resolve(Node? node) =>
        node is not null && _byNodeTag.TryGetValue(node.Tag, out var value) ? value : null;

    /// <summary>True when the nodes this binding holds are no longer the tree's — the tree was emptied behind its
    /// back, or a held node no longer answers. Seen after cancelling a sketch drawn on the fly in the dialog's
    /// curve block: the sketch task's rollback can take the tree's nodes with it. A stale binding must be rebuilt,
    /// never written through.</summary>
    public bool IsStale()
    {
        if (_rows.Count == 0)
            return false;

        try
        {
            if (_tree.RootNode is null)
                return true;

            _ = _rows[0].Node.DisplayText;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void Clear()
    {
        // Collect the roots before deleting any: DeleteNode invalidates the node it removes, so walking the
        // sibling chain while deleting from it would step off a dead node. (There is no Tree.Clear().)
        // A node already gone (see IsStale) ends the walk or is skipped: the rebuild that follows is the point.
        var roots = new List<Node>();
        try
        {
            for (var node = _tree.RootNode; node is not null; node = node.NextSiblingNode)
                roots.Add(node);
        }
        catch (NXException)
        {
        }

        foreach (var root in roots)
        {
            try
            {
                _tree.DeleteNode(root);
            }
            catch (NXException)
            {
            }
        }

        _byNodeTag.Clear();
        _rows.Clear();
    }
}
