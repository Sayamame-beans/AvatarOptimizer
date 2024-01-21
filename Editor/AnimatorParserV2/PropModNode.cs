using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Debug = System.Diagnostics.Debug;

namespace Anatawa12.AvatarOptimizer.AnimatorParsersV2
{
    /// <summary>
    /// This class represents a node in the property modification tree.
    ///
    /// In AnimatorParser V2, Modifications of each property are represented as a tree to make it possible to
    /// remove modifications of a property.
    ///
    /// This class is the abstract class for the nodes.
    ///
    /// Most nodes are immutable but some nodes are mutable.
    /// </summary>
    internal abstract class PropModNode<T> : IErrorContext
    {
        public T ConstantValue => Constant.Value;
        public bool IsConstant => Constant.IsConstant;

        /// <summary>
        /// Returns true if this node is always applied. For inactive nodes, this returns false.
        /// </summary>
        public abstract bool AppliedAlways { get; }
        public abstract ConstantInfo<T> Constant { get; }
        public abstract IEnumerable<ObjectReference> ContextReferences { get; }
    }

    internal readonly struct ConstantInfo<T>
    {
        public bool IsConstant { get; }
        private readonly T _value;

        public T Value
        {
            get
            {
                if (!IsConstant) throw new InvalidOperationException("Not Constant");
                return _value;
            }
        }

        public static ConstantInfo<T> Variable => default;

        public ConstantInfo(T value)
        {
            _value = value;
            IsConstant = true;
        }

        public bool TryGetValue(out T o)
        {
            o = _value;
            return IsConstant;
        }
    }

    internal static class NodeImplUtils
    {
        public static ConstantInfo<T> ConstantInfoForSideBySide<T>(IEnumerable<PropModNode<T>> nodes)
        {
            using (var enumerator = nodes.GetEnumerator())
            {
                Debug.Assert(enumerator.MoveNext());

                if (!enumerator.Current.Constant.TryGetValue(out var value))
                    return ConstantInfo<T>.Variable;

                while (enumerator.MoveNext())
                {
                    if (!enumerator.Current.Constant.TryGetValue(out var otherValue))
                        return ConstantInfo<T>.Variable;

                    if (!EqualityComparer<T>.Default.Equals(value, otherValue))
                        return ConstantInfo<T>.Variable;
                }

                return new ConstantInfo<T>(value);
            }
        }

        public static ConstantInfo<T> ConstantInfoForOverriding<T, TLayer>(IEnumerable<TLayer> layersReversed)
            where TLayer : ILayer<T>
        {
            T value = default;
            bool initialized = false;

            foreach (var layer in layersReversed)
            {
                switch (layer.Weight)
                {
                    case AnimatorWeightState.AlwaysOne:
                    case AnimatorWeightState.EitherZeroOrOne:
                        if (!layer.Node.Constant.TryGetValue(out var otherValue)) return ConstantInfo<T>.Variable;

                        if (layer.Node.AppliedAlways && layer.Weight == AnimatorWeightState.AlwaysOne &&
                            layer.BlendingMode == AnimatorLayerBlendingMode.Override)
                        {
                            // the layer is always applied at the highest property.
                            return new ConstantInfo<T>(otherValue);
                        }

                        // partially applied constants so save that value and continue.
                        if (!initialized)
                        {
                            value = otherValue;
                            initialized = true;
                        }
                        else
                        {
                            if (!EqualityComparer<T>.Default.Equals(value, otherValue))
                                return ConstantInfo<T>.Variable;
                        }

                        break;
                    case AnimatorWeightState.Variable:
                        return ConstantInfo<T>.Variable;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return new ConstantInfo<T>(value);
        }
    }

    internal interface ILayer<T>
    {
        AnimatorWeightState Weight { get; }
        AnimatorLayerBlendingMode BlendingMode { get; }
        PropModNode<T> Node { get; }
    }

    internal sealed class RootPropModNode<T> : PropModNode<T>, IErrorContext
    {
        private readonly List<ComponentPropModNode<T>> _children = new List<ComponentPropModNode<T>>();

        public RootPropModNode(params RootPropModNode<T>[] props)
        {
            foreach (var prop in props)
            foreach (var child in prop._children)
                Add(child);
        }

        public override bool AppliedAlways => _children.All(x => x.AppliedAlways);
        public override IEnumerable<ObjectReference> ContextReferences => _children.SelectMany(x => x.ContextReferences);
        public override ConstantInfo<T> Constant => NodeImplUtils.ConstantInfoForSideBySide(_children);

        public IEnumerable<Component> SourceComponents => _children.Select(x => x.Component);

        public void Add(ComponentPropModNode<T> value)
        {
            _children.Add(value);
        }
    }

    internal abstract class ImmutablePropModNode<T> : PropModNode<T>
    {
    }

    internal class FloatAnimationCurveNode : ImmutablePropModNode<float>
    {
        public AnimationCurve Curve { get; }
        public AnimationClip Clip { get; }

        [CanBeNull]
        public static FloatAnimationCurveNode Create([NotNull] AnimationClip clip, EditorCurveBinding binding)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) return null;
            if (curve.keys.Length == 0) return null;
            return new FloatAnimationCurveNode(clip, curve);
        }

        private FloatAnimationCurveNode(AnimationClip clip, AnimationCurve curve)
        {
            Debug.Assert(curve.keys.Length > 0);
            Clip = clip;
            Curve = curve;
            _constantInfo = new Lazy<ConstantInfo<float>>(() => ParseProperty(curve), isThreadSafe: false);
        }

        private readonly Lazy<ConstantInfo<float>> _constantInfo;

        public override bool AppliedAlways => true;
        public override ConstantInfo<float> Constant => _constantInfo.Value;
        public override IEnumerable<ObjectReference> ContextReferences => new []{ ObjectRegistry.GetReference(Clip) };

        private static ConstantInfo<float> ParseProperty(AnimationCurve curve)
        {
            if (curve.keys.Length == 1) return new ConstantInfo<float>(curve.keys[0].value);

            float constValue = 0;
            foreach (var (preKey, postKey) in curve.keys.ZipWithNext())
            {
                var preWeighted = preKey.weightedMode == WeightedMode.Out || preKey.weightedMode == WeightedMode.Both;
                var postWeighted = postKey.weightedMode == WeightedMode.In || postKey.weightedMode == WeightedMode.Both;

                if (preKey.value.CompareTo(postKey.value) != 0) return ConstantInfo<float>.Variable;
                constValue = preKey.value;
                // it's constant
                if (float.IsInfinity(preKey.outWeight) || float.IsInfinity(postKey.inTangent)) continue;
                if (preKey.outTangent == 0 && postKey.inTangent == 0) continue;
                if (preWeighted && postWeighted && preKey.outWeight == 0 && postKey.inWeight == 0) continue;
                return ConstantInfo<float>.Variable;
            }

            return new ConstantInfo<float>(constValue);
        }
    }

    internal class BlendTreeNode<T> : ImmutablePropModNode<T>
    {
        private readonly IEnumerable<ImmutablePropModNode<T>> _children;
        private readonly BlendTreeType _blendTreeType;

        public BlendTreeNode(IEnumerable<ImmutablePropModNode<T>> children, BlendTreeType blendTreeType, bool partial)
        {
            // expected to pass list or array
            // ReSharper disable once PossibleMultipleEnumeration
            Debug.Assert(children.Any());
            // ReSharper disable once PossibleMultipleEnumeration
            _children = children;
            _blendTreeType = blendTreeType;

            _appliedAlways = new Lazy<bool>(() =>
            {
                if (!WeightSumIsOne) return false;
                return !partial && _children.All(x => x.AppliedAlways);
            }, isThreadSafe: false);

            _constantInfo = new Lazy<ConstantInfo<T>>(() =>
            {
                if (!WeightSumIsOne) return ConstantInfo<T>.Variable;
                return NodeImplUtils.ConstantInfoForSideBySide(_children);
            }, isThreadSafe: false);
        }


        private bool WeightSumIsOne => _blendTreeType != BlendTreeType.Direct;

        private readonly Lazy<bool> _appliedAlways;
        private readonly Lazy<ConstantInfo<T>> _constantInfo;

        public override bool AppliedAlways => _appliedAlways.Value;
        public override IEnumerable<ObjectReference> ContextReferences => _children.SelectMany(x => x.ContextReferences);
        public override ConstantInfo<T> Constant => _constantInfo.Value;
    }

    abstract class ComponentPropModNode<T> : PropModNode<T>
    {
        protected ComponentPropModNode([NotNull] Component component)
        {
            if (!component) throw new ArgumentNullException(nameof(component));
            Component = component;
        }

        public Component Component { get; }

        public override IEnumerable<ObjectReference> ContextReferences => new [] { ObjectRegistry.GetReference(Component) };
    }

    class VariableComponentPropModNode<T> : ComponentPropModNode<T>
    {
        public VariableComponentPropModNode([NotNull] Component component) : base(component)
        {
        }

        public override bool AppliedAlways => false;
        public override ConstantInfo<T> Constant => ConstantInfo<T>.Variable;
    }

    class AnimationComponentPropModNode<T> : ComponentPropModNode<T>
    {
        private readonly ImmutablePropModNode<T> _animation;

        public AnimationComponentPropModNode([NotNull] Component component, ImmutablePropModNode<T> animation) : base(component)
        {
            _animation = animation;
        }

        public override bool AppliedAlways => false;
        public override ConstantInfo<T> Constant => _animation.Constant;
    }
}
