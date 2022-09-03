﻿using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.Constraints.Contact;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace BepuPhysics.Trees
{
    public interface IOverlapHandler
    {
        void Handle(int indexA, int indexB);
    }


    partial struct Tree
    {
        //TODO: This contains a lot of empirically tested implementations on much older runtimes.
        //I suspect results would be different on modern versions of ryujit. In particular, recursion is very unlikely to be the fastest approach.
        //(I don't immediately recall what made the non-recursive version slower last time- it's possible that it was making use of stackalloc and I hadn't yet realized that it 
        //requires zeroing, or something along those lines.)

        //Note that all of these implementations make use of a fully generic handler. It could be dumping to a list, or it could be directly processing the results- at this
        //level of abstraction we don't know or care. It's up to the user to use a handler which maximizes performance if they want it. We'll be using this in the broad phase.
        unsafe void DispatchTestForLeaf<TOverlapHandler>(int leafIndex, ref NodeChild leafChild, int nodeIndex, ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {
            if (nodeIndex < 0)
            {
                results.Handle(leafIndex, Encode(nodeIndex));
            }
            else
            {
                TestLeafAgainstNode(leafIndex, ref leafChild, nodeIndex, ref results);
            }
        }
        unsafe void TestLeafAgainstNode<TOverlapHandler>(int leafIndex, ref NodeChild leafChild, int nodeIndex, ref TOverlapHandler results)
            where TOverlapHandler : IOverlapHandler
        {
            ref var node = ref Nodes[nodeIndex];
            ref var a = ref node.A;
            ref var b = ref node.B;
            //Despite recursion, leafBounds should remain in L1- it'll be used all the way down the recursion from here.
            //However, while we likely loaded child B when we loaded child A, there's no guarantee that it will stick around.
            //Reloading that in the event of eviction would require more work than keeping the derived data on the stack.
            //TODO: this is some pretty questionable microtuning. It's not often that the post-leaf-found recursion will be long enough to evict L1. Definitely test it.
            var bIndex = b.Index;
            var aIntersects = BoundingBox.IntersectsUnsafe(leafChild, a);
            var bIntersects = BoundingBox.IntersectsUnsafe(leafChild, b);
            if (aIntersects)
            {
                DispatchTestForLeaf(leafIndex, ref leafChild, a.Index, ref results);
            }
            if (bIntersects)
            {
                DispatchTestForLeaf(leafIndex, ref leafChild, bIndex, ref results);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void DispatchTestForNodes<TOverlapHandler>(ref NodeChild a, ref NodeChild b, ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {
            if (a.Index >= 0)
            {
                if (b.Index >= 0)
                {
                    GetOverlapsBetweenDifferentNodes(ref Nodes[a.Index], ref Nodes[b.Index], ref results);
                }
                else
                {
                    //leaf B versus node A.
                    TestLeafAgainstNode(Encode(b.Index), ref b, a.Index, ref results);
                }
            }
            else if (b.Index >= 0)
            {
                //leaf A versus node B.
                TestLeafAgainstNode(Encode(a.Index), ref a, b.Index, ref results);
            }
            else
            {
                //Two leaves.
                results.Handle(Encode(a.Index), Encode(b.Index));
            }
        }

        unsafe void GetOverlapsBetweenDifferentNodes<TOverlapHandler>(ref Node a, ref Node b, ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {
            //There are no shared children, so test them all.
            ref var aa = ref a.A;
            ref var ab = ref a.B;
            ref var ba = ref b.A;
            ref var bb = ref b.B;
            var aaIntersects = BoundingBox.IntersectsUnsafe(aa, ba);
            var abIntersects = BoundingBox.IntersectsUnsafe(aa, bb);
            var baIntersects = BoundingBox.IntersectsUnsafe(ab, ba);
            var bbIntersects = BoundingBox.IntersectsUnsafe(ab, bb);

            if (aaIntersects)
            {
                DispatchTestForNodes(ref aa, ref ba, ref results);
            }
            if (abIntersects)
            {
                DispatchTestForNodes(ref aa, ref bb, ref results);
            }
            if (baIntersects)
            {
                DispatchTestForNodes(ref ab, ref ba, ref results);
            }
            if (bbIntersects)
            {
                DispatchTestForNodes(ref ab, ref bb, ref results);
            }
        }

        unsafe void GetOverlapsInNode<TOverlapHandler>(ref Node node, ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {

            ref var a = ref node.A;
            ref var b = ref node.B;

            var ab = BoundingBox.IntersectsUnsafe(a, b);

            if (a.Index >= 0)
                GetOverlapsInNode(ref Nodes[a.Index], ref results);
            if (b.Index >= 0)
                GetOverlapsInNode(ref Nodes[b.Index], ref results);

            //Test all different nodes.
            if (ab)
            {
                DispatchTestForNodes(ref a, ref b, ref results);
            }

        }

        public unsafe void GetSelfOverlaps<TOverlapHandler>(ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            GetOverlapsInNode(ref Nodes[0], ref results);
        }

        unsafe void GetOverlapsWithLeaf<TOverlapHandler>(ref TOverlapHandler results, NodeChild leaf, int nodeToTest, ref QuickList<int> stack) where TOverlapHandler : IOverlapHandler
        {
            var leafIndex = Encode(leaf.Index);
            Debug.Assert(stack.Count == 0);
            while (true)
            {
                ref var node = ref Nodes[nodeToTest];
                var a = BoundingBox.IntersectsUnsafe(leaf, node.A);
                var b = BoundingBox.IntersectsUnsafe(leaf, node.B);
                var aIsInternal = node.A.Index >= 0;
                var bIsInternal = node.B.Index >= 0;
                var intersectedInternalA = a && aIsInternal;
                var intersectedInternalB = b && bIsInternal;
                if (intersectedInternalA && intersectedInternalB)
                {
                    nodeToTest = node.A.Index;
                    stack.AllocateUnsafely() = node.B.Index;
                }
                else
                {
                    if (a && !aIsInternal)
                    {
                        results.Handle(leafIndex, Encode(node.A.Index));
                    }
                    if (b && !bIsInternal)
                    {
                        results.Handle(leafIndex, Encode(node.B.Index));
                    }
                    if (intersectedInternalA || intersectedInternalB)
                    {
                        nodeToTest = intersectedInternalA ? node.A.Index : node.B.Index;
                    }
                    else
                    {
                        //The current traversal step doesn't offer a next step; pull from the stack.
                        if (!stack.TryPop(out nodeToTest))
                        {
                            //Nothing left to test against this leaf! Done!
                            break;
                        }
                    }
                }
            }
        }
        struct StackEntry
        {
            public int A;
            public int B;
        }


        public unsafe void GetSelfOverlaps2<TOverlapHandler>(ref TOverlapHandler results, BufferPool pool) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            QuickList<StackEntry> stack = new(NodeCount, pool);
            QuickList<int> leafStack = new(NodeCount, pool);
            StackEntry nextTest = default;

            while (true)
            {
                if (nextTest.A == nextTest.B)
                {
                    //Self test.
                    //Possible sources of further stack entries in a self test:
                    //1) Child A and B are both internal and their bounds intersect.
                    //2) Child A is internal.
                    //3) Child B is internal.
                    //We can also spawn leaf-subtree tests if:
                    //1) Child A and B have intersecting bounds, and only one of them is a leaf.
                    //We can spawn direct leaf tests if:
                    //1) Child A and B have intersecting bounds, and both are leaves.
                    //Note that we never need to push an entry with differing A and B indices to the stack *from a self test*. There can only be one such entry created from any self test, and it's always visited next.
                    //Non-self tests will generate *only* results with differing A and B indices.
                    ref var node = ref Nodes[nextTest.A];
                    var abIntersect = BoundingBox.IntersectsUnsafe(node.A, node.B);
                    var aIsInternal = node.A.Index >= 0;
                    var bIsInternal = node.B.Index >= 0;

                    if (aIsInternal)
                    {
                        nextTest.A = node.A.Index;
                        nextTest.B = node.A.Index;

                        if (bIsInternal)
                        {
                            ref var stackB = ref stack.AllocateUnsafely();
                            stackB.A = node.B.Index;
                            stackB.B = node.B.Index;

                            if (abIntersect)
                            {
                                ref var stackEntry = ref stack.AllocateUnsafely();
                                stackEntry.A = node.A.Index;
                                stackEntry.B = node.B.Index;
                            }
                        }
                        else if (abIntersect)
                        {
                            GetOverlapsWithLeaf(ref results, node.B, node.A.Index, ref leafStack);
                        }
                    }
                    else if (bIsInternal)
                    {
                        nextTest.A = node.B.Index;
                        nextTest.B = node.B.Index;

                        if (abIntersect)
                        {
                            GetOverlapsWithLeaf(ref results, node.A, node.B.Index, ref leafStack);
                        }
                    }
                    else if (abIntersect)
                    {
                        //Both children are leaves. They have overlapping bounds, so...
                        results.Handle(Encode(node.A.Index), Encode(node.B.Index));
                        //No new tests available, so grab from the stack.
                        if (!stack.TryPop(out nextTest))
                            break;
                    }
                }
                else
                {
                    //Not a self test!
                    //Possible sources of stack entry:
                    //1) AA intersection, both internal
                    //2) AB intersection, both internal
                    //3) BA intersection, both internal
                    //4) BB intersection, both internal
                    ref var n0 = ref Nodes[nextTest.A];
                    ref var n1 = ref Nodes[nextTest.B];
                    var aaIntersects = BoundingBox.IntersectsUnsafe(n0.A, n1.A);
                    var abIntersects = BoundingBox.IntersectsUnsafe(n0.A, n1.B);
                    var baIntersects = BoundingBox.IntersectsUnsafe(n0.B, n1.A);
                    var bbIntersects = BoundingBox.IntersectsUnsafe(n0.B, n1.B);
                    var n0AIsInternal = n0.A.Index >= 0;
                    var n0BIsInternal = n0.B.Index >= 0;
                    var n1AIsInternal = n1.A.Index >= 0;
                    var n1BIsInternal = n1.B.Index >= 0;
                    //The first test which generates a stack candidate gets the nextTest; the rest get pushed to the stack.
                    int previousStackGeneratorCount = 0;
                    if (aaIntersects)
                    {
                        if (n0AIsInternal && n1AIsInternal)
                        {
                            ++previousStackGeneratorCount;
                            nextTest.A = n0.A.Index;
                            nextTest.B = n1.A.Index;
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0AIsInternal)
                                GetOverlapsWithLeaf(ref results, n1.A, n0.A.Index, ref leafStack);
                            else if (n1AIsInternal)
                                GetOverlapsWithLeaf(ref results, n0.A, n1.A.Index, ref leafStack);
                            else //Both are leaves.
                                results.Handle(Encode(n0.A.Index), Encode(n1.A.Index));
                        }
                    }
                    if (abIntersects)
                    {
                        if (n0AIsInternal && n1BIsInternal)
                        {
                            if (previousStackGeneratorCount++ == 0)
                            {
                                nextTest.A = n0.A.Index;
                                nextTest.B = n1.B.Index;
                            }
                            else
                            {
                                ref var stackEntry = ref stack.AllocateUnsafely();
                                stackEntry.A = n0.A.Index;
                                stackEntry.B = n1.B.Index;
                            }
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0AIsInternal)
                                GetOverlapsWithLeaf(ref results, n1.B, n0.A.Index, ref leafStack);
                            else if (n1BIsInternal)
                                GetOverlapsWithLeaf(ref results, n0.A, n1.B.Index, ref leafStack);
                            else //Both are leaves.
                                results.Handle(Encode(n0.A.Index), Encode(n1.B.Index));
                        }
                    }
                    if (baIntersects)
                    {
                        if (n0BIsInternal && n1AIsInternal)
                        {
                            if (previousStackGeneratorCount++ == 0)
                            {
                                nextTest.A = n0.B.Index;
                                nextTest.B = n1.A.Index;
                            }
                            else
                            {
                                ref var stackEntry = ref stack.AllocateUnsafely();
                                stackEntry.A = n0.B.Index;
                                stackEntry.B = n1.A.Index;
                            }
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0BIsInternal)
                                GetOverlapsWithLeaf(ref results, n1.A, n0.B.Index, ref leafStack);
                            else if (n1AIsInternal)
                                GetOverlapsWithLeaf(ref results, n0.B, n1.A.Index, ref leafStack);
                            else //Both are leaves.
                                results.Handle(Encode(n0.B.Index), Encode(n1.A.Index));
                        }
                    }
                    if (bbIntersects)
                    {
                        if (n0BIsInternal && n1BIsInternal)
                        {
                            if (previousStackGeneratorCount++ == 0)
                            {
                                nextTest.A = n0.B.Index;
                                nextTest.B = n1.B.Index;
                            }
                            else
                            {
                                ref var stackEntry = ref stack.AllocateUnsafely();
                                stackEntry.A = n0.B.Index;
                                stackEntry.B = n1.B.Index;
                            }
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0BIsInternal)
                                GetOverlapsWithLeaf(ref results, n1.B, n0.B.Index, ref leafStack);
                            else if (n1BIsInternal)
                                GetOverlapsWithLeaf(ref results, n0.B, n1.B.Index, ref leafStack);
                            else //Both are leaves.
                                results.Handle(Encode(n0.B.Index), Encode(n1.B.Index));
                        }
                    }
                    if (previousStackGeneratorCount == 0)
                    {
                        //None of the candidates generated a next step, so grab from the stack.
                        if (!stack.TryPop(out nextTest))
                            break;
                    }
                }

            }
            leafStack.Dispose(pool);
            stack.Dispose(pool);

        }

        enum StackEntryType
        {
            SelfTest,
            Crossover,
            Leaf,
        }
        struct ComboStackEntry
        {
            public int EncodedA;
            public int B;

            public StackEntryType Type => EncodedA < 0 ? StackEntryType.Leaf : EncodedA == B ? StackEntryType.SelfTest : StackEntryType.Crossover;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ComboStackEntry CreateForLeafTest(int leafParentIndex, bool leafChildIsB, int nodeIndex)
            {
                ComboStackEntry entry;
                //Upper bit flags this stack entry as a leaf versus node test.
                entry.EncodedA = leafParentIndex | (1 << 31);
                if (leafChildIsB)
                    entry.EncodedA |= 1 << 30;
                entry.B = nodeIndex;
                return entry;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ComboStackEntry CreateForSelfTest(int nodeIndex)
            {
                ComboStackEntry entry;
                entry.EncodedA = nodeIndex;
                entry.B = nodeIndex;
                return entry;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ComboStackEntry CreateCrossover(int a, int b)
            {
                ComboStackEntry entry;
                entry.EncodedA = a;
                entry.B = b;
                return entry;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref NodeChild GetLeafChild(ref Tree tree)
            {
                var parentNodeIndex = EncodedA & 0x3FFF_FFFF;
                var leafIsChildB = (EncodedA & (1 << 30)) != 0;
                ref var parent = ref tree.Nodes[parentNodeIndex];
                return ref leafIsChildB ? ref parent.B : ref parent.A;
            }
        }

        public unsafe void GetSelfOverlaps3<TOverlapHandler>(ref TOverlapHandler results, BufferPool pool) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            QuickList<ComboStackEntry> stack = new(NodeCount, pool);
            QuickList<int> leafStack = new(NodeCount, pool);
            ComboStackEntry nextTest = default;

            while (true)
            {
                switch (nextTest.Type)
                {
                    case StackEntryType.SelfTest:
                        {
                            //Self test.
                            //Possible sources of further stack entries in a self test:
                            //1) Child A and B are both internal and their bounds intersect.
                            //2) Child A is internal.
                            //3) Child B is internal.
                            //We can also spawn leaf-subtree tests if:
                            //1) Child A and B have intersecting bounds, and only one of them is a leaf.
                            //We can spawn direct leaf tests if:
                            //1) Child A and B have intersecting bounds, and both are leaves.
                            //Note that we never need to push an entry with differing A and B indices to the stack *from a self test*. There can only be one such entry created from any self test, and it's always visited next.
                            //Non-self tests will generate *only* results with differing A and B indices.
                            ref var node = ref Nodes[nextTest.B];
                            //GetOverlapsInNode(ref node, ref results);
                            //if (!stack.TryPop(out nextTest))
                            //{
                            //    goto Terminate;
                            //}
                            //break;
                            var abIntersect = BoundingBox.IntersectsUnsafe(node.A, node.B);
                            var aIsInternal = node.A.Index >= 0;
                            var bIsInternal = node.B.Index >= 0;

                            if (aIsInternal)
                            {
                                if (bIsInternal)
                                {
                                    stack.AllocateUnsafely() = ComboStackEntry.CreateForSelfTest(node.B.Index);
                                    if (abIntersect)
                                    {
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateCrossover(node.A.Index, node.B.Index);
                                    }
                                }
                                else if (abIntersect)
                                {
                                    stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(nextTest.B, true, node.A.Index);
                                }
                                nextTest = ComboStackEntry.CreateForSelfTest(node.A.Index);
                            }
                            else if (bIsInternal)
                            {
                                if (abIntersect)
                                {
                                    stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(nextTest.EncodedA, false, node.B.Index);
                                }
                                nextTest = ComboStackEntry.CreateForSelfTest(node.B.Index);
                            }
                            else if (abIntersect)
                            {
                                //Both children are leaves. They have overlapping bounds, so...
                                results.Handle(Encode(node.A.Index), Encode(node.B.Index));
                                //No new tests available, so grab from the stack.
                                if (!stack.TryPop(out nextTest))
                                {
                                    goto Terminate;
                                }
                            }
                        }
                        break;
                    case StackEntryType.Crossover:
                        {
                            //Not a self test!
                            //Possible sources of stack entry:
                            //1) AA intersection, both internal
                            //2) AB intersection, both internal
                            //3) BA intersection, both internal
                            //4) BB intersection, both internal
                            var n0Index = nextTest.EncodedA;
                            var n1Index = nextTest.B;
                            ref var n0 = ref Nodes[nextTest.EncodedA];
                            ref var n1 = ref Nodes[nextTest.B];

                            //GetOverlapsBetweenDifferentNodes(ref n0, ref n1, ref results);
                            //if (!stack.TryPop(out nextTest))
                            //{
                            //    goto Terminate;
                            //}
                            //break;
                            var aaIntersects = BoundingBox.IntersectsUnsafe(n0.A, n1.A);
                            var abIntersects = BoundingBox.IntersectsUnsafe(n0.A, n1.B);
                            var baIntersects = BoundingBox.IntersectsUnsafe(n0.B, n1.A);
                            var bbIntersects = BoundingBox.IntersectsUnsafe(n0.B, n1.B);
                            var n0AIsInternal = n0.A.Index >= 0;
                            var n0BIsInternal = n0.B.Index >= 0;
                            var n1AIsInternal = n1.A.Index >= 0;
                            var n1BIsInternal = n1.B.Index >= 0;
                            //The first test which generates a stack candidate gets the nextTest; the rest get pushed to the stack.
                            int previousStackGeneratorCount = 0;
                            if (aaIntersects)
                            {
                                if (n0AIsInternal && n1AIsInternal)
                                {
                                    ++previousStackGeneratorCount;
                                    nextTest = ComboStackEntry.CreateCrossover(n0.A.Index, n1.A.Index);
                                }
                                else
                                {
                                    //At least one is a leaf.
                                    if (n0AIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n1Index, false, n0.A.Index);
                                    else if (n1AIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n0Index, false, n1.A.Index);
                                    else //Both are leaves.
                                        results.Handle(Encode(n0.A.Index), Encode(n1.A.Index));
                                }
                            }
                            if (abIntersects)
                            {
                                if (n0AIsInternal && n1BIsInternal)
                                {
                                    if (previousStackGeneratorCount++ == 0)
                                    {
                                        nextTest = ComboStackEntry.CreateCrossover(n0.A.Index, n1.B.Index);
                                    }
                                    else
                                    {
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateCrossover(n0.A.Index, n1.B.Index);
                                    }
                                }
                                else
                                {
                                    //At least one is a leaf.
                                    if (n0AIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n1Index, true, n0.A.Index);
                                    else if (n1BIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n0Index, false, n1.B.Index);
                                    else //Both are leaves.
                                        results.Handle(Encode(n0.A.Index), Encode(n1.B.Index));
                                }
                            }
                            if (baIntersects)
                            {
                                if (n0BIsInternal && n1AIsInternal)
                                {
                                    if (previousStackGeneratorCount++ == 0)
                                    {
                                        nextTest = ComboStackEntry.CreateCrossover(n0.B.Index, n1.A.Index);
                                    }
                                    else
                                    {
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateCrossover(n0.B.Index, n1.A.Index);
                                    }
                                }
                                else
                                {
                                    //At least one is a leaf.
                                    if (n0BIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n1Index, false, n0.B.Index);
                                    else if (n1AIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n0Index, true, n1.A.Index);
                                    else //Both are leaves.
                                        results.Handle(Encode(n0.B.Index), Encode(n1.A.Index));
                                }
                            }
                            if (bbIntersects)
                            {
                                if (n0BIsInternal && n1BIsInternal)
                                {
                                    if (previousStackGeneratorCount++ == 0)
                                    {
                                        nextTest = ComboStackEntry.CreateCrossover(n0.B.Index, n1.B.Index);
                                    }
                                    else
                                    {
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateCrossover(n0.B.Index, n1.B.Index);
                                    }
                                }
                                else
                                {
                                    //At least one is a leaf.
                                    if (n0BIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n1Index, true, n0.B.Index);
                                    else if (n1BIsInternal)
                                        stack.AllocateUnsafely() = ComboStackEntry.CreateForLeafTest(n0Index, true, n1.B.Index);
                                    else //Both are leaves.
                                        results.Handle(Encode(n0.B.Index), Encode(n1.B.Index));
                                }
                            }
                            if (previousStackGeneratorCount == 0)
                            {
                                //None of the candidates generated a next step, so grab from the stack.
                                if (!stack.TryPop(out nextTest))
                                {
                                    goto Terminate;
                                }
                            }
                        }
                        break;
                    default:
                        {
                            //Leaf-node test. Swap over to our own local stack. Equivalent visitation order.
                            ref var leafChild = ref nextTest.GetLeafChild(ref this);
                            var nodeToTest = nextTest.B;
                            //DispatchTestForLeaf(Encode(leafChild.Index), ref leafChild, nodeToTest, ref results);
                            //GetOverlapsWithLeaf(ref results, leafChild, nodeToTest, ref leafStack);
                            var leafIndex = Encode(leafChild.Index);
                            Debug.Assert(leafStack.Count == 0);
                            while (true)
                            {
                                ref var node = ref Nodes[nodeToTest];
                                var aIntersects = BoundingBox.IntersectsUnsafe(leafChild, node.A);
                                var bIntersects = BoundingBox.IntersectsUnsafe(leafChild, node.B);
                                var aIsInternal = node.A.Index >= 0;
                                var bIsInternal = node.B.Index >= 0;
                                var intersectedInternalA = aIntersects && aIsInternal;
                                var intersectedInternalB = bIntersects && bIsInternal;

                                if (aIntersects && !aIsInternal)
                                {
                                    results.Handle(leafIndex, Encode(node.A.Index));
                                }
                                if (bIntersects && !bIsInternal)
                                {
                                    results.Handle(leafIndex, Encode(node.B.Index));
                                }

                                if (intersectedInternalA)
                                {
                                    nodeToTest = node.A.Index;
                                    if (intersectedInternalB)
                                    {
                                        leafStack.AllocateUnsafely() = node.B.Index;
                                    }
                                }
                                else if (intersectedInternalB)
                                {
                                    nodeToTest = node.B.Index;
                                }
                                else if (!leafStack.TryPop(out nodeToTest))
                                {
                                    //Nothing left to test against this leaf! Done!
                                    break;
                                }
                            }
                            if (!stack.TryPop(out nextTest))
                            {
                                goto Terminate;
                            }
                        }
                        break;
                }
            }
        Terminate:
            leafStack.Dispose(pool);
            stack.Dispose(pool);

        }

        struct LeafNodeStackEntry
        {
            public uint EncodedLeafParentIndex;
            public int NodeIndex;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LeafNodeStackEntry(int leafParentIndex, bool leafChildIsB, int nodeIndex)
            {
                EncodedLeafParentIndex = (uint)leafParentIndex;
                if (leafChildIsB)
                    EncodedLeafParentIndex |= 1u << 31;
                NodeIndex = nodeIndex;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ref NodeChild GetChild(ref Tree tree)
            {
                var parentNodeIndex = EncodedLeafParentIndex & 0x7FFF_FFFF;
                var leafIsChildB = EncodedLeafParentIndex > 0x7FFF_FFFF;
                ref var parent = ref tree.Nodes[parentNodeIndex];
                return ref leafIsChildB ? ref parent.B : ref parent.A;
            }
        }


        public unsafe void GetSelfOverlapsContiguousPrepass<TOverlapHandler>(ref TOverlapHandler results, BufferPool pool) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            //A recursive self test will at some point visit all nodes with certainty. Instead of framing it as a recursive test at all, do a prepass that's just a contiguous iteration.
            QuickList<StackEntry> crossoverStack = new(NodeCount, pool);
            QuickList<LeafNodeStackEntry> leafNodeStack = new(NodeCount, pool);
            for (int i = 0; i < NodeCount; ++i)
            {
                ref var node = ref Nodes[i];
                var a = node.A.Index;
                var b = node.B.Index;
                var aIsInternal = a >= 0;
                var bIsInternal = b >= 0;
                if (aIsInternal && bIsInternal)
                {
                    if (BoundingBox.IntersectsUnsafe(node.A, node.B))
                    {
                        ref var stackEntry = ref crossoverStack.AllocateUnsafely();
                        stackEntry.A = a;
                        stackEntry.B = b;
                    }
                }
                else if (aIsInternal || bIsInternal)
                {
                    //One is a leaf, one is internal.
                    leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(i, aIsInternal, aIsInternal ? a : b);
                }
                else
                {
                    //Both are leaves.
                    results.Handle(Encode(a), Encode(b));
                }
            }

            //Now, we need to complete all crossovers and leaf-node tests. Note that leaf-node tests can only generate leaf-node or leaf-leaf tests, they never produce crossovers.
            //In contrast, crossovers can generate leaf-node tests. So do crossovers first so we'll have every leaf-node test in the stack ready to go.
            if (crossoverStack.TryPop(out var nextCrossover))
            {
                while (true)
                {
                    //Possible sources of stack entry:
                    //1) AA intersection, both internal
                    //2) AB intersection, both internal
                    //3) BA intersection, both internal
                    //4) BB intersection, both internal
                    var parent0 = nextCrossover.A;
                    var parent1 = nextCrossover.B;
                    ref var n0 = ref Nodes[parent0];
                    ref var n1 = ref Nodes[parent1];
                    var aaIntersects = BoundingBox.IntersectsUnsafe(n0.A, n1.A);
                    var abIntersects = BoundingBox.IntersectsUnsafe(n0.A, n1.B);
                    var baIntersects = BoundingBox.IntersectsUnsafe(n0.B, n1.A);
                    var bbIntersects = BoundingBox.IntersectsUnsafe(n0.B, n1.B);
                    var n0A = n0.A.Index;
                    var n0B = n0.B.Index;
                    var n1A = n1.A.Index;
                    var n1B = n1.B.Index;
                    var n0AIsInternal = n0A >= 0;
                    var n0BIsInternal = n0B >= 0;
                    var n1AIsInternal = n1A >= 0;
                    var n1BIsInternal = n1B >= 0;
                    //The first test which generates a stack candidate gets the nextTest; the rest get pushed to the stack.
                    int previousStackGeneratorCount = 0;
                    if (aaIntersects)
                    {
                        if (n0AIsInternal && n1AIsInternal)
                        {
                            ++previousStackGeneratorCount;
                            nextCrossover.A = n0A;
                            nextCrossover.B = n1A;
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0AIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent1, false, n0A);
                            else if (n1AIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent0, false, n1A);
                            else //Both are leaves.
                                results.Handle(Encode(n0A), Encode(n1A));
                        }
                    }
                    if (abIntersects)
                    {
                        if (n0AIsInternal && n1BIsInternal)
                        {
                            if (previousStackGeneratorCount++ == 0)
                            {
                                nextCrossover.A = n0A;
                                nextCrossover.B = n1B;
                            }
                            else
                            {
                                ref var stackEntry = ref crossoverStack.AllocateUnsafely();
                                stackEntry.A = n0A;
                                stackEntry.B = n1B;
                            }
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0AIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent1, true, n0A);
                            else if (n1BIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent0, false, n1B);
                            else //Both are leaves.
                                results.Handle(Encode(n0A), Encode(n1B));
                        }
                    }
                    if (baIntersects)
                    {
                        if (n0BIsInternal && n1AIsInternal)
                        {
                            if (previousStackGeneratorCount++ == 0)
                            {
                                nextCrossover.A = n0B;
                                nextCrossover.B = n1A;
                            }
                            else
                            {
                                ref var stackEntry = ref crossoverStack.AllocateUnsafely();
                                stackEntry.A = n0B;
                                stackEntry.B = n1A;
                            }
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0BIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent1, false, n0B);
                            else if (n1AIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent0, true, n1A);
                            else //Both are leaves.
                                results.Handle(Encode(n0B), Encode(n1A));
                        }
                    }
                    if (bbIntersects)
                    {
                        if (n0BIsInternal && n1BIsInternal)
                        {
                            if (previousStackGeneratorCount++ == 0)
                            {
                                nextCrossover.A = n0B;
                                nextCrossover.B = n1B;
                            }
                            else
                            {
                                ref var stackEntry = ref crossoverStack.AllocateUnsafely();
                                stackEntry.A = n0B;
                                stackEntry.B = n1B;
                            }
                        }
                        else
                        {
                            //At least one is a leaf.
                            if (n0BIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent1, true, n0B);
                            else if (n1BIsInternal)
                                leafNodeStack.AllocateUnsafely() = new LeafNodeStackEntry(parent0, true, n1B);
                            else //Both are leaves.
                                results.Handle(Encode(n0B), Encode(n1B));
                        }
                    }
                    if (previousStackGeneratorCount == 0)
                    {
                        //None of the candidates generated a next step, so grab from the stack.
                        if (!crossoverStack.TryPop(out nextCrossover))
                            break;
                    }
                }
            }
            crossoverStack.Dispose(pool);
            QuickList<int> stack = new(NodeCount, pool);

            for (int i = 0; i < leafNodeStack.Count; ++i)
            {
                var leafNodeTarget = leafNodeStack[i];
                ref var leafChild = ref leafNodeTarget.GetChild(ref this);
                var leafIndex = Encode(leafChild.Index);
                var nodeToTest = leafNodeTarget.NodeIndex;
                Debug.Assert(stack.Count == 0);
                while (true)
                {
                    //Now process all the leaf nodes!
                    ref var node = ref Nodes[nodeToTest];
                    var a = node.A.Index;
                    var b = node.B.Index;
                    var aIntersected = BoundingBox.IntersectsUnsafe(leafChild, node.A);
                    var bIntersected = BoundingBox.IntersectsUnsafe(leafChild, node.B);
                    var aIsInternal = a >= 0;
                    var bIsInternal = b >= 0;
                    var intersectedInternalA = aIntersected && aIsInternal;
                    var intersectedInternalB = bIntersected && bIsInternal;
                    if (intersectedInternalA && intersectedInternalB)
                    {
                        nodeToTest = a;
                        stack.AllocateUnsafely() = b;
                    }
                    else
                    {
                        if (aIntersected && !aIsInternal)
                        {
                            results.Handle(leafIndex, Encode(a));
                        }
                        if (bIntersected && !bIsInternal)
                        {
                            results.Handle(leafIndex, Encode(b));
                        }
                        if (intersectedInternalA || intersectedInternalB)
                        {
                            nodeToTest = intersectedInternalA ? a : b;
                        }
                        else
                        {
                            //The current traversal step doesn't offer a next step; pull from the stack.
                            if (!stack.TryPop(out nodeToTest))
                            {
                                //Nothing left to test against this leaf! Done!
                                break;
                            }
                        }
                    }
                }
            }
            leafNodeStack.Dispose(pool);
            stack.Dispose(pool);
        }

        public unsafe void GetSelfOverlapsPrepassWithRecursion<TOverlapHandler>(ref TOverlapHandler results, BufferPool pool) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            var leafStack = new QuickList<int>(NodeCount, pool);
            //A recursive self test will at some point visit all nodes with certainty. Instead of framing it as a recursive test at all, do a prepass that's just a contiguous iteration.
            for (int i = 0; i < NodeCount; ++i)
            {
                ref var node = ref Nodes[i];
                var a = node.A.Index;
                var b = node.B.Index;
                var aIsInternal = a >= 0;
                var bIsInternal = b >= 0;
                if (aIsInternal && bIsInternal)
                {
                    if (BoundingBox.IntersectsUnsafe(node.A, node.B))
                    {
                        GetOverlapsBetweenDifferentNodes(ref Nodes[a], ref Nodes[b], ref results);
                    }
                }
                else if (aIsInternal || bIsInternal)
                {
                    //One is a leaf, one is internal.
                    GetOverlapsWithLeaf(ref results, aIsInternal ? node.B : node.A, aIsInternal ? a : b, ref leafStack);
                }
                else
                {
                    //Both are leaves.
                    results.Handle(Encode(a), Encode(b));
                }
            }
            leafStack.Dispose(pool);
        }




        ref struct SelfTest<TOverlapHandler> where TOverlapHandler : IOverlapHandler
        {
            public Buffer<Node> Nodes;
            public ref TOverlapHandler results;

            unsafe void DispatchTestForLeaf(int leafIndex, ref NodeChild leafChild, int nodeIndex)
            {
                if (nodeIndex < 0)
                {
                    results.Handle(leafIndex, Encode(nodeIndex));
                }
                else
                {
                    TestLeafAgainstNode(leafIndex, ref leafChild, nodeIndex);
                }
            }
            unsafe void TestLeafAgainstNode(int leafIndex, ref NodeChild leafChild, int nodeIndex)
            {
                ref var node = ref Nodes[nodeIndex];
                ref var a = ref node.A;
                ref var b = ref node.B;
                //Despite recursion, leafBounds should remain in L1- it'll be used all the way down the recursion from here.
                //However, while we likely loaded child B when we loaded child A, there's no guarantee that it will stick around.
                //Reloading that in the event of eviction would require more work than keeping the derived data on the stack.
                //TODO: this is some pretty questionable microtuning. It's not often that the post-leaf-found recursion will be long enough to evict L1. Definitely test it.
                var bIndex = b.Index;
                var aIntersects = BoundingBox.IntersectsUnsafe(leafChild, a);
                var bIntersects = BoundingBox.IntersectsUnsafe(leafChild, b);
                if (aIntersects)
                {
                    DispatchTestForLeaf(leafIndex, ref leafChild, a.Index);
                }
                if (bIntersects)
                {
                    DispatchTestForLeaf(leafIndex, ref leafChild, bIndex);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            unsafe void DispatchTestForNodes(ref NodeChild a, ref NodeChild b)
            {
                if (a.Index >= 0)
                {
                    if (b.Index >= 0)
                    {
                        GetOverlapsBetweenDifferentNodes(ref Nodes[a.Index], ref Nodes[b.Index]);
                    }
                    else
                    {
                        //leaf B versus node A.
                        TestLeafAgainstNode(Encode(b.Index), ref b, a.Index);
                    }
                }
                else if (b.Index >= 0)
                {
                    //leaf A versus node B.
                    TestLeafAgainstNode(Encode(a.Index), ref a, b.Index);
                }
                else
                {
                    //Two leaves.
                    results.Handle(Encode(a.Index), Encode(b.Index));
                }
            }

            unsafe void GetOverlapsBetweenDifferentNodes(ref Node a, ref Node b)
            {
                //There are no shared children, so test them all.
                ref var aa = ref a.A;
                ref var ab = ref a.B;
                ref var ba = ref b.A;
                ref var bb = ref b.B;
                var aaIntersects = BoundingBox.IntersectsUnsafe(aa, ba);
                var abIntersects = BoundingBox.IntersectsUnsafe(aa, bb);
                var baIntersects = BoundingBox.IntersectsUnsafe(ab, ba);
                var bbIntersects = BoundingBox.IntersectsUnsafe(ab, bb);

                if (aaIntersects)
                {
                    DispatchTestForNodes(ref aa, ref ba);
                }
                if (abIntersects)
                {
                    DispatchTestForNodes(ref aa, ref bb);
                }
                if (baIntersects)
                {
                    DispatchTestForNodes(ref ab, ref ba);
                }
                if (bbIntersects)
                {
                    DispatchTestForNodes(ref ab, ref bb);
                }
            }

            public unsafe void GetOverlapsInNode(ref Node node)
            {

                ref var a = ref node.A;
                ref var b = ref node.B;

                var ab = BoundingBox.IntersectsUnsafe(a, b);

                if (a.Index >= 0)
                    GetOverlapsInNode(ref Nodes[a.Index]);
                if (b.Index >= 0)
                    GetOverlapsInNode(ref Nodes[b.Index]);

                //Test all different nodes.
                if (ab)
                {
                    DispatchTestForNodes(ref a, ref b);
                }
            }
        }
        public unsafe void GetSelfOverlaps4<TOverlapHandler>(ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            SelfTest<TOverlapHandler> hmm;
            hmm.Nodes = Nodes;
            hmm.results = ref results;
            hmm.GetOverlapsInNode(ref Nodes[0]);
        }

        ref struct SelfTest5<TOverlapHandler> where TOverlapHandler : IOverlapHandler
        {
            public Buffer<Node> Nodes;
            public ref TOverlapHandler Results;
            //When using a leaf-node test.
            public int LeafIndex;
            public ref NodeChild LeafChild;

            unsafe void DispatchTestForLeaf(int nodeIndex)
            {
                if (nodeIndex < 0)
                {
                    Results.Handle(LeafIndex, Encode(nodeIndex));
                }
                else
                {
                    TestLeafAgainstNode(nodeIndex);
                }
            }
            unsafe void TestLeafAgainstNode(int nodeIndex)
            {
                ref var node = ref Nodes[nodeIndex];
                ref var a = ref node.A;
                ref var b = ref node.B;
                //Despite recursion, leafBounds should remain in L1- it'll be used all the way down the recursion from here.
                //However, while we likely loaded child B when we loaded child A, there's no guarantee that it will stick around.
                //Reloading that in the event of eviction would require more work than keeping the derived data on the stack.
                //TODO: this is some pretty questionable microtuning. It's not often that the post-leaf-found recursion will be long enough to evict L1. Definitely test it.
                var bIndex = b.Index;
                var aIntersects = BoundingBox.IntersectsUnsafe(LeafChild, a);
                var bIntersects = BoundingBox.IntersectsUnsafe(LeafChild, b);
                if (aIntersects)
                {
                    DispatchTestForLeaf(a.Index);
                }
                if (bIntersects)
                {
                    DispatchTestForLeaf(bIndex);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            unsafe void DispatchTestForNodes(ref NodeChild a, ref NodeChild b)
            {
                if (a.Index >= 0)
                {
                    if (b.Index >= 0)
                    {
                        GetOverlapsBetweenDifferentNodes(ref Nodes[a.Index], ref Nodes[b.Index]);
                    }
                    else
                    {
                        //leaf B versus node A.
                        LeafIndex = Encode(b.Index);
                        LeafChild = ref b;
                        TestLeafAgainstNode(a.Index);
                    }
                }
                else if (b.Index >= 0)
                {
                    //leaf A versus node B.
                    LeafIndex = Encode(a.Index);
                    LeafChild = ref a;
                    TestLeafAgainstNode(b.Index);
                }
                else
                {
                    //Two leaves.
                    Results.Handle(Encode(a.Index), Encode(b.Index));
                }
            }

            unsafe void GetOverlapsBetweenDifferentNodes(ref Node a, ref Node b)
            {
                //There are no shared children, so test them all.
                ref var aa = ref a.A;
                ref var ab = ref a.B;
                ref var ba = ref b.A;
                ref var bb = ref b.B;
                var aaIntersects = BoundingBox.IntersectsUnsafe(aa, ba);
                var abIntersects = BoundingBox.IntersectsUnsafe(aa, bb);
                var baIntersects = BoundingBox.IntersectsUnsafe(ab, ba);
                var bbIntersects = BoundingBox.IntersectsUnsafe(ab, bb);

                if (aaIntersects)
                {
                    DispatchTestForNodes(ref aa, ref ba);
                }
                if (abIntersects)
                {
                    DispatchTestForNodes(ref aa, ref bb);
                }
                if (baIntersects)
                {
                    DispatchTestForNodes(ref ab, ref ba);
                }
                if (bbIntersects)
                {
                    DispatchTestForNodes(ref ab, ref bb);
                }
            }

            public unsafe void GetOverlapsInNode(ref Node node)
            {

                ref var a = ref node.A;
                ref var b = ref node.B;

                var ab = BoundingBox.IntersectsUnsafe(a, b);

                if (a.Index >= 0)
                    GetOverlapsInNode(ref Nodes[a.Index]);
                if (b.Index >= 0)
                    GetOverlapsInNode(ref Nodes[b.Index]);

                //Test all different nodes.
                if (ab)
                {
                    DispatchTestForNodes(ref a, ref b);
                }
            }
        }
        public unsafe void GetSelfOverlaps5<TOverlapHandler>(ref TOverlapHandler results) where TOverlapHandler : IOverlapHandler
        {
            //If there are less than two leaves, there can't be any overlap.
            //This provides a guarantee that there are at least 2 children in each internal node considered by GetOverlapsInNode.
            if (LeafCount < 2)
                return;

            SelfTest5<TOverlapHandler> hmm = default;
            hmm.Nodes = Nodes;
            hmm.Results = ref results;
            hmm.GetOverlapsInNode(ref Nodes[0]);
        }

    }
}
