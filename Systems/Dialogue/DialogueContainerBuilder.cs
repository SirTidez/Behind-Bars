using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Dialogue;
#else
using ScheduleOne.Dialogue;
#endif

namespace Behind_Bars.Systems.Dialogue
{
    /// <summary>
    /// Builder to compose a choice-based DialogueContainer entirely from code.
    /// Standalone implementation based on S1API DialogueContainerBuilder.
    /// </summary>
    public sealed class DialogueContainerBuilder
    {
        private readonly Dictionary<string, NodeSpec> _nodes = new Dictionary<string, NodeSpec>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LinkSpec> _links = new List<LinkSpec>();
        private bool _allowExit = true;

        /// <summary>
        /// Adds a dialogue node by label with the text shown in the bubble/UI.
        /// </summary>
        public DialogueContainerBuilder AddNode(string nodeLabel, string text, Action<ChoiceList> choices = null)
        {
            if (string.IsNullOrEmpty(nodeLabel))
                return this;
            if (!_nodes.TryGetValue(nodeLabel, out var node))
            {
                node = new NodeSpec(this, nodeLabel, text ?? string.Empty);
                _nodes[nodeLabel] = node;
            }
            else
            {
                node.Text = text ?? string.Empty;
            }

            if (choices != null)
            {
                var list = (ChoiceList)typeof(ChoiceList)
                    .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new Type[]{ typeof(NodeSpec) }, null)
                    .Invoke(new object[]{ node });
                choices(list);
            }
            return this;
        }

        /// <summary>
        /// Sets whether the player can exit while this container is active.
        /// </summary>
        public DialogueContainerBuilder SetAllowExit(bool allow)
        {
            _allowExit = allow;
            return this;
        }

        /// <summary>
        /// Builds a ScriptableObject DialogueContainer.
        /// </summary>
        public DialogueContainer Build(string containerName)
        {
            var container = ScriptableObject.CreateInstance<DialogueContainer>();
            container.name = string.IsNullOrEmpty(containerName) ? "CustomContainer" : containerName;

            // Build node data
            var nodeData = new List<DialogueNodeData>();
            foreach (var kvp in _nodes)
            {
                var spec = kvp.Value;
                var node = new DialogueNodeData
                {
                    Guid = spec.Guid,
                    DialogueNodeLabel = spec.Label,
                    DialogueText = spec.Text,
                    Position = Vector2.zero,
                    choices = BuildChoices(spec)
                };
                nodeData.Add(node);
            }
            container.DialogueNodeData = ToIl2CppList(nodeData);

            // Build links
            var links = new List<NodeLinkData>();
            foreach (var link in _links)
            {
                if (!_nodes.TryGetValue(link.FromNodeLabel, out var fromNode))
                    continue;
                var baseChoice = fromNode.GetChoice(link.ChoiceLabel);
                if (baseChoice == null)
                    continue;
                if (!_nodes.TryGetValue(link.ToNodeLabel, out var toNode))
                    continue;

                links.Add(new NodeLinkData
                {
                    BaseDialogueOrBranchNodeGuid = fromNode.Guid,
                    BaseChoiceOrOptionGUID = baseChoice.Guid,
                    TargetNodeGuid = toNode.Guid
                });
            }
            container.NodeLinks = ToIl2CppList(links);

            // AllowExit flag
            container.SetAllowExit(_allowExit);

            return container;
        }

        private static DialogueChoiceData[] BuildChoices(NodeSpec node)
        {
            var result = new List<DialogueChoiceData>();
            foreach (var c in node.Choices)
            {
                result.Add(new DialogueChoiceData
                {
                    Guid = c.Guid,
                    ChoiceLabel = c.Label,
                    ChoiceText = c.Text,
                    ShowWorldspaceDialogue = true
                });
            }
            return result.ToArray();
        }

        /// <summary>
        /// Helper class used during choice configuration.
        /// </summary>
        public sealed class ChoiceList
        {
            private readonly NodeSpec _node;
            private ChoiceList(NodeSpec node) { _node = node; }

            /// <summary>
            /// Adds a choice with a label and shown text and links it to a target node label.
            /// </summary>
            public ChoiceList Add(string choiceLabel, string shownText, string targetNodeLabel = null)
            {
                if (string.IsNullOrEmpty(choiceLabel))
                    return this;
                var choice = _node.AddChoice(choiceLabel, shownText ?? string.Empty);
                if (!string.IsNullOrEmpty(targetNodeLabel))
                {
                    _node.Builder._links.Add(new LinkSpec(_node.Label, choice.Label, targetNodeLabel));
                }
                return this;
            }
        }

        private sealed class NodeSpec
        {
            internal readonly string Guid = System.Guid.NewGuid().ToString();
            internal readonly DialogueContainerBuilder Builder;
            internal readonly List<ChoiceSpec> Choices = new List<ChoiceSpec>();
            internal string Label;
            internal string Text;

            internal NodeSpec(DialogueContainerBuilder builder, string label, string text)
            {
                Builder = builder;
                Label = label;
                Text = text;
            }

            internal ChoiceSpec AddChoice(string label, string text)
            {
                var c = new ChoiceSpec(label, text);
                Choices.Add(c);
                return c;
            }

            internal ChoiceSpec GetChoice(string label) => Choices.Find(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class ChoiceSpec
        {
            internal readonly string Guid = System.Guid.NewGuid().ToString();
            internal readonly string Label;
            internal readonly string Text;
            internal ChoiceSpec(string label, string text)
            {
                Label = label;
                Text = text;
            }
        }

        private readonly struct LinkSpec
        {
            internal readonly string FromNodeLabel;
            internal readonly string ChoiceLabel;
            internal readonly string ToNodeLabel;
            internal LinkSpec(string from, string choice, string to)
            {
                FromNodeLabel = from;
                ChoiceLabel = choice;
                ToNodeLabel = to;
            }
        }

#if !MONO
        private static Il2CppSystem.Collections.Generic.List<T> ToIl2CppList<T>(System.Collections.Generic.List<T> source)
        {
            var list = new Il2CppSystem.Collections.Generic.List<T>();
            if (source == null)
                return list;
            for (int i = 0; i < source.Count; i++)
                list.Add(source[i]);
            return list;
        }
#else
        private static System.Collections.Generic.List<T> ToIl2CppList<T>(System.Collections.Generic.List<T> source)
        {
            return source;
        }
#endif
    }
}

