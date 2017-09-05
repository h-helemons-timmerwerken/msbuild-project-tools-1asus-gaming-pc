using System;
using System.Collections.Generic;

namespace MSBuildProjectTools.LanguageServer.SemanticModel
{
    using MSBuildExpressions;

    /// <summary>
    ///     Extension methods for <see cref="ExpressionNode"/>.
    /// </summary>
    public static class ExpressionExtensions
    {
        /// <summary>
        ///     Enumerate all of the node's ancestor nodes, up to the root node.
        /// </summary>
        /// <param name="node">
        ///     The target node.
        /// </param>
        /// <returns>
        ///     A sequence of ancestor nodes.
        /// </returns>
        public static IEnumerable<ExpressionNode> AncestorNodes(this ExpressionNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            ExpressionNode parent = node.Parent;
            while (parent != null)
            {
                yield return parent;

                parent = parent.Parent;
            }
        }

        /// <summary>
        ///     Recursively enumerate the node's descendant nodes.
        /// </summary>
        /// <param name="node">
        ///     The target node.
        /// </param>
        /// <returns>
        ///     A sequence of descendant nodes (depth-first).
        /// </returns>
        public static IEnumerable<ExpressionNode> DescendantNodes(this ExpressionNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            ExpressionContainerNode containerNode = node as ExpressionContainerNode;
            if (containerNode == null)
                yield break;

            foreach (ExpressionNode childNode in containerNode.Children)
            {
                yield return node;

                foreach (ExpressionNode descendant in childNode.DescendantNodes())
                    yield return descendant;
            }
        }

        /// <summary>
        ///     Find the list item at (or close to) the specified absolute position within the source text.
        /// </summary>
        /// <param name="list">
        ///     The <see cref="SimpleList"/> to search.
        /// </param>
        /// <param name="atPosition">
        ///     The absolute position (0-based).
        /// </param>
        /// <returns>
        ///     The <see cref="SimpleListItem"/>, or <c>null</c> if there is no item at the specified absolute position.
        /// </returns>
        public static SimpleListItem FindItemAt(this SimpleList list, int atPosition)
        {
            if (list == null)
                throw new ArgumentNullException(nameof(list));

            if (atPosition < list.AbsoluteStart || atPosition > list.AbsoluteEnd)
                return null;

            ExpressionNode nodeAtPosition = list.Children.FindLast(
                node => node.AbsoluteStart <= atPosition
            );
            if (nodeAtPosition is SimpleListItem itemAtPosition)
                return itemAtPosition;

            if (nodeAtPosition is SimpleListSeparator separatorAtPosition)
            {
                // If the position is on or before a separator then choose the preceding item; otherwise, choose the next item.
                int separatorPosition = separatorAtPosition.AbsoluteStart + separatorAtPosition.SeparatorOffset;

                return (atPosition <= separatorPosition)
                    ? separatorAtPosition.PreviousSibling as SimpleListItem
                    : separatorAtPosition.NextSibling as SimpleListItem;
            }

            throw new InvalidOperationException(
                $"Encountered unexpected node type '{nodeAtPosition.GetType().FullName}' inside a SimpleList expression."
            );
        }
    }
}
