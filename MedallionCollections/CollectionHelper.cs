using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Medallion.Collections
{
    /// <summary>
    /// Contains utility and extension methods for working with .NET collections
    /// </summary>
    public static class CollectionHelper
    {
        #region ---- Partition ----
        /// <summary>
        /// Splits the given <paramref name="source"/> sequence into a series of <see cref="List{T}"/>s
        /// of length <paramref name="partitionSize"/>. Note that the final partition may be less than
        /// <paramref name="partitionSize"/>
        /// </summary>
        public static IEnumerable<List<T>> Partition<T>(this IEnumerable<T> source, int partitionSize)
        {
            if (source == null) { throw new ArgumentNullException(nameof(source)); }
            if (partitionSize < 1) { throw new ArgumentOutOfRangeException(paramName: nameof(partitionSize), message: $"Value must be positive (got {partitionSize})"); }
            
            return PartitionIterator(source, partitionSize);
        }

        private static IEnumerable<List<T>> PartitionIterator<T>(this IEnumerable<T> source, int partitionSize)
        {
            // we like initializing our lists with capacity to avoid resizes. However, we don't want to trigger
            // OutOfMemory if the partition size is huge
            var initialCapacity = Math.Min(partitionSize, 1024);

            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    var partition = new List<T>(capacity: initialCapacity) { enumerator.Current };
                    for (var i = 1; i < partitionSize && enumerator.MoveNext(); ++i)
                    {
                        partition.Add(enumerator.Current);
                    }
                    yield return partition;
                }
            }
        }
        #endregion

        #region ---- Append ----
        /// <summary>
        /// As <see cref="Enumerable.Concat{TSource}(IEnumerable{TSource}, IEnumerable{TSource})"/>, but with better
        /// performance for repeated calls. See http://blogs.msdn.com/b/wesdyer/archive/2007/03/23/all-about-iterators.aspx
        /// </summary>
        public static IEnumerable<TElement> Append<TElement>(this IEnumerable<TElement> first, IEnumerable<TElement> second)
        {
            if (first == null) { throw new ArgumentNullException(nameof(first)); }
            if (second == null) { throw new ArgumentNullException(nameof(second)); }

            return new AppendEnumerable<TElement>(first, second);
        }

        /// <summary>
        /// As <see cref="Enumerable.Concat{TSource}(IEnumerable{TSource}, IEnumerable{TSource})"/>, but appends only one element
        /// Optimized for repeated calls. See http://blogs.msdn.com/b/wesdyer/archive/2007/03/23/all-about-iterators.aspx
        /// </summary>
        public static IEnumerable<TElement> Append<TElement>(this IEnumerable<TElement> sequence, TElement next)
        {
            if (sequence == null) { throw new ArgumentNullException(nameof(sequence)); }

            return new AppendOneEnumerable<TElement>(sequence, next);
        }

        /// <summary>
        /// As <see cref="Append{TElement}(IEnumerable{TElement}, IEnumerable{TElement})"/>, but prepends the elements
        /// instead
        /// </summary>
        public static IEnumerable<TElement> Prepend<TElement>(this IEnumerable<TElement> second, IEnumerable<TElement> first)
        {
            if (first == null) { throw new ArgumentNullException(nameof(first)); }
            if (second == null) { throw new ArgumentNullException(nameof(second)); }

            return new AppendEnumerable<TElement>(first, second);
        }

        /// <summary>
        /// As <see cref="Append{TElement}(IEnumerable{TElement}, TElement)"/>, but prepends an element instead
        /// </summary>
        public static IEnumerable<TElement> Prepend<TElement>(this IEnumerable<TElement> sequence, TElement previous)
        {
            if (sequence == null) { throw new ArgumentNullException(nameof(sequence)); }

            return new PrependOneEnumerable<TElement>(previous, sequence);
        }

        private interface IAppendEnumerable<out TElement>
        {
            IEnumerable<TElement> PreviousElements { get; }
            TElement PreviousElement { get; }
            IEnumerable<TElement> NextElements { get; }
            TElement NextElement { get; }
        }

        private abstract class AppendEnumerableBase<TElement> : IAppendEnumerable<TElement>, IEnumerable<TElement>
        {
            public abstract TElement NextElement { get; }
            public abstract IEnumerable<TElement> NextElements { get; }
            public abstract TElement PreviousElement { get; }
            public abstract IEnumerable<TElement> PreviousElements { get; }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.AsEnumerable().GetEnumerator();
            }

            IEnumerator<TElement> IEnumerable<TElement>.GetEnumerator()
            {
                // we special case the basic case so that it doesn't even need to create the stack
                if (!(this.PreviousElements is IAppendEnumerable<TElement>)
                    && !(this.NextElements is IAppendEnumerable<TElement>))
                {
                    if (this.PreviousElements != null)
                    {
                        foreach (var element in this.PreviousElements)
                        {
                            yield return element;
                        }
                    }
                    else
                    {
                        yield return this.PreviousElement;
                    }

                    if (this.NextElements != null)
                    {
                        foreach (var element in this.NextElements)
                        {
                            yield return element;
                        }
                    }
                    else
                    {
                        yield return this.NextElement;
                    }

                    yield break;
                }

                // the algorithm here keeps 2 pieces of state:
                // (1) the current node in the append enumerable binary tree
                // (2) a stack of nodes we have to come back to to process the right subtree (nexts)
                // the steps are as follows, starting with current as the root of the tree:
                // (1) if the left subtree is a leaf, yield it
                // (2) otherwise, push current on the stack and set current = the left subtree
                // (3) if the right subtree is a leaf, yield it
                // (4) otherwise, set current = right subtree
                // (5) if both subtrees were leaves, set current = stack.Pop(), or exit if stack is empty

                IAppendEnumerable<TElement> currentAppendEnumerable = this;
                var enumerableStack = new Stack<IAppendEnumerable<TElement>>();
                while (true)
                {
                    if (currentAppendEnumerable != null)
                    {
                        var previous = currentAppendEnumerable.PreviousElements;
                        if (previous == null)
                        {
                            yield return currentAppendEnumerable.PreviousElement;
                        }
                        else
                        {
                            var previousAppendEnumerable = previous as IAppendEnumerable<TElement>;
                            if (previousAppendEnumerable != null)
                            {
                                enumerableStack.Push(currentAppendEnumerable);
                                currentAppendEnumerable = previousAppendEnumerable;
                                continue;
                            }

                            foreach (var previousElement in currentAppendEnumerable.PreviousElements)
                            {
                                yield return previousElement;
                            }
                        }
                    }

                    if (currentAppendEnumerable == null)
                    {
                        if (enumerableStack.Count == 0)
                        {
                            yield break;
                        }
                        currentAppendEnumerable = enumerableStack.Pop();
                    }

                    var next = currentAppendEnumerable.NextElements;
                    if (next == null)
                    {
                        yield return currentAppendEnumerable.NextElement;
                    }
                    else
                    {
                        var nextAppendEnumerable = currentAppendEnumerable.NextElements as IAppendEnumerable<TElement>;
                        if (nextAppendEnumerable != null)
                        {
                            currentAppendEnumerable = nextAppendEnumerable;
                            continue;
                        }

                        foreach (var nextElement in currentAppendEnumerable.NextElements)
                        {
                            yield return nextElement;
                        }
                    }

                    currentAppendEnumerable = null;
                }
            }
        }

        private sealed class AppendEnumerable<TElement> : AppendEnumerableBase<TElement>
        {
            private readonly IEnumerable<TElement> previous, next;

            public AppendEnumerable(IEnumerable<TElement> previous, IEnumerable<TElement> next)
            {
                this.previous = previous;
                this.next = next;
            }

            public override TElement NextElement { get { throw new InvalidOperationException(); } }
            public override IEnumerable<TElement> NextElements { get { return this.next; } }
            public override TElement PreviousElement { get { throw new InvalidOperationException(); } }
            public override IEnumerable<TElement> PreviousElements { get { return this.previous; } }
        }

        private sealed class AppendOneEnumerable<TElement> : AppendEnumerableBase<TElement>
        {
            private readonly IEnumerable<TElement> previous;
            private readonly TElement next;

            public AppendOneEnumerable(IEnumerable<TElement> previous, TElement next)
            {
                this.previous = previous;
                this.next = next;
            }

            public override TElement NextElement { get { return this.next; } }
            public override IEnumerable<TElement> NextElements { get { return null; } }
            public override TElement PreviousElement { get { throw new InvalidOperationException(); } }
            public override IEnumerable<TElement> PreviousElements { get { return this.previous; } }
        }

        private sealed class PrependOneEnumerable<TElement> : AppendEnumerableBase<TElement>
        {
            private readonly TElement previous;
            private readonly IEnumerable<TElement> next;

            public PrependOneEnumerable(TElement previous, IEnumerable<TElement> next)
            {
                this.previous = previous;
                this.next = next;
            }

            public override TElement NextElement { get { throw new InvalidOperationException(); } }
            public override IEnumerable<TElement> NextElements { get { return this.next; } }
            public override TElement PreviousElement { get { return this.previous; ; } }
            public override IEnumerable<TElement> PreviousElements { get { return null; } }
        }
        #endregion
        
        #region ---- MaxBy / MinBy ----
        /// <summary>
        /// As <see cref="Enumerable.Max{TSource, TResult}(IEnumerable{TSource}, Func{TSource, TResult})"/>, but returns the
        /// maximum item from the original sequence instead of the value projected by <paramref name="keySelector"/>. The
        /// optional <paramref name="comparer"/> allows key comparisons to be specified
        /// </summary>
        public static TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer = null)
        {
            if (source == null) { throw new ArgumentNullException(nameof(source)); }
            if (keySelector == null) { throw new ArgumentNullException(nameof(keySelector)); }

            var cmp = comparer ?? Comparer<TKey>.Default;
            using (var enumerator = source.GetEnumerator())
            {
                var isNullable = default(TSource) == null;
                if (!enumerator.MoveNext())
                {
                    // just like native Min/Max, the empty sequence returns null for nullable types
                    // and throws hard for non-nullable types
                    if (isNullable) { return default(TSource); }
                    throw new InvalidOperationException("Sequence contains no elements");
                }

                var bestValue = enumerator.Current;
                var bestKey = keySelector(bestValue);
                while (enumerator.MoveNext())
                {
                    var value = enumerator.Current;
                    var key = keySelector(value);

                    if (isNullable
                        // like Min/Max, nulls are excluded from the comparison
                        ? (bestKey == null || (cmp.Compare(key, bestKey) > 0 && key != null))
                        : cmp.Compare(key, bestKey) > 0)
                    {
                        bestValue = value;
                        bestKey = key;
                    }
                }

                return bestValue;
            }
        }

        /// <summary>
        /// As <see cref="Enumerable.Min{TSource, TResult}(IEnumerable{TSource}, Func{TSource, TResult})"/>, but returns the
        /// minimum item from the original sequence instead of the value projected by <paramref name="keySelector"/>. The
        /// optional <paramref name="comparer"/> allows key comparisons to be specified
        /// </summary>
        public static TSource MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer = null)
        {
            if (source == null) { throw new ArgumentNullException(nameof(source)); }
            if (keySelector == null) { throw new ArgumentNullException(nameof(keySelector)); }

            var cmp = comparer ?? Comparer<TKey>.Default;
            using (var enumerator = source.GetEnumerator())
            {
                var isNullable = default(TSource) == null;
                if (!enumerator.MoveNext())
                {
                    // just like native Min/Max, the empty sequence returns null for nullable types
                    // and throws hard for non-nullable types
                    if (isNullable) { return default(TSource); }
                    throw new InvalidOperationException("Sequence contains no elements");
                }

                var bestValue = enumerator.Current;
                var bestKey = keySelector(bestValue);
                while (enumerator.MoveNext())
                {
                    var value = enumerator.Current;
                    var key = keySelector(value);

                    if (isNullable
                        // like Min/Max, nulls are excluded from the comparison
                        ? (bestKey == null || (cmp.Compare(key, bestKey) < 0 && key != null))
                        : cmp.Compare(key, bestKey) < 0)
                    {
                        bestValue = value;
                        bestKey = key;
                    }
                }

                return bestValue;
            }
        }
        #endregion

        #region ---- CollectionEquals ----
        /// <summary>
        /// Determines whether <paramref name="this"/> and <paramref name="that"/> are equal in the sense of having the exact same
        /// elements. Unlike <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}, IEnumerable{TSource})"/>,
        /// this method disregards order. Unlike <see cref="ISet{T}.SetEquals(IEnumerable{T})"/>, this method does not disregard duplicates.
        /// An optional <paramref name="comparer"/> allows the equality semantics for the elements to be specified
        /// </summary>
        public static bool CollectionEquals<TElement>(this IEnumerable<TElement> @this, IEnumerable<TElement> that, IEqualityComparer<TElement> comparer = null)
        {
            if (@this == null) { throw new ArgumentNullException(nameof(@this)); }
            if (that == null) { throw new ArgumentNullException(nameof(that)); }

            if (object.ReferenceEquals(@this, that))
            {
                return true;
            }

            // FastCount optimization: If both of the collections are materialized and have counts, 
            // we can exit very quickly if those counts differ
            int thisCount, thatCount;
            var hasThisCount = TryFastCount(@this, out thisCount);
            bool hasThatCount;
            if (hasThisCount)
            {
                hasThatCount = TryFastCount(that, out thatCount);
                if (hasThatCount)
                {
                    if (thisCount != thatCount)
                    {
                        return false;
                    }
                    if (thisCount == 0)
                    {
                        return true;
                    }
                }
            }
            else
            {
                hasThatCount = false;
            }

            var cmp = comparer ?? EqualityComparer<TElement>.Default;

            var itemsEnumerated = 0;
            // SequenceEqual optimization: we reduce/avoid hashing
            // the collections have common prefixes, at the cost of only one
            // extra Equals() call in the case where the prefixes are not common
            using (var thisEnumerator = @this.GetEnumerator())
            using (var thatEnumerator = that.GetEnumerator())
            {
                while (true)
                {
                    var thisFinished = !thisEnumerator.MoveNext();
                    var thatFinished = !thatEnumerator.MoveNext();

                    if (thisFinished)
                    {
                        // either this shorter than that, or the two were sequence-equal
                        return thatFinished;
                    }
                    if (thatFinished)
                    {
                        // that shorter than this
                        return false;
                    }

                    // keep track of this so that we can factor it into count-based 
                    // logic below
                    ++itemsEnumerated;

                    if (!cmp.Equals(thisEnumerator.Current, thatEnumerator.Current))
                    {
                        // Special case
                        if (@this is SortedSet<TElement> thisSorted && that is SortedSet<TElement> thatSorted)
                        {
                            if (cmp.Equals(EqualityComparer<TElement>.Default) && 
                                (                                
                                    thisSorted.Comparer.Equals(thatSorted.Comparer) ||
                                    Math.Sign(thisSorted.Comparer.Compare(thisEnumerator.Current, thatEnumerator.Current)) ==
                                    Math.Sign(thatSorted.Comparer.Compare(thisEnumerator.Current, thatEnumerator.Current))
                                )
                            )
                            {
                                // If two sorted collections, are sorted the same way, then we know 
                                // the collections dont match
                                return false;
                            }
                        }

                        break; // prefixes were not equal
                    }
                }

                // now, build a dictionary of item => count out of one collection and then
                // probe it with the other collection to look for mismatches

                // Build/Probe Choice optimization: if we know the count of one collection, we should
                // use the other collection to build the dictionary. That way we can bail immediately if
                // we see too few or too many items
                CountingSet<TElement> elementCounts;
                IEnumerator<TElement> probeSide;
                if (hasThisCount)
                {
                    // we know this's count => use that as the build side
                    probeSide = thisEnumerator;
                    var remaining = thisCount - itemsEnumerated;
                    if (hasThatCount)
                    {
                        // if we have both counts, that means they must be equal or we would have already
                        // exited. However, in this case, we know exactly the capacity needed for the dictionary
                        // so we can avoid resizing
                        elementCounts = new CountingSet<TElement>(capacity: remaining, comparer: cmp);
                        do
                        {
                            elementCounts.Increment(thatEnumerator.Current);
                        }
                        while (thatEnumerator.MoveNext());
                    }
                    else
                    {
                        elementCounts = TryBuildElementCountsWithKnownCount(thatEnumerator, remaining, cmp);
                    }
                }
                else if (TryFastCount(that, out thatCount))
                {
                    // we know that's count => use this as the build side
                    probeSide = thatEnumerator;
                    var remaining = thatCount - itemsEnumerated;
                    elementCounts = TryBuildElementCountsWithKnownCount(thisEnumerator, remaining, cmp);
                }
                else
                {
                    // when we don't know either count, just use that as the build side arbitrarily
                    probeSide = thisEnumerator;
                    elementCounts = new CountingSet<TElement>(cmp);
                    do
                    {
                        elementCounts.Increment(thatEnumerator.Current);
                    }
                    while (thatEnumerator.MoveNext());
                }

                // check whether we failed to construct a dictionary. This happens when we know
                // one of the counts and we detect, during construction, that the counts are unequal
                if (elementCounts == null)
                {
                    return false;
                }

                // probe the dictionary with the probe side enumerator
                do
                {
                    if (elementCounts.Decrement(probeSide.Current) < 0)
                    {
                        // element in probe not in build => not equal
                        return false;
                    }
                }
                while (probeSide.MoveNext());

                // we are equal only if the loop above completely cleared out the dictionary
                return elementCounts.IsEmpty;
            }
        }

        /// <summary>
        /// Constructs a count dictionary, staying mindful of the known number of elements
        /// so that we bail early (returning null) if we detect a count mismatch
        /// </summary>
        private static CountingSet<TKey> TryBuildElementCountsWithKnownCount<TKey>(
            IEnumerator<TKey> elements, 
            int remaining,
            IEqualityComparer<TKey> comparer)
        {
            if (remaining == 0)
            {
                // don't build the dictionary at all if nothing should be in it
                return null;
            }

            var elementCounts = new CountingSet<TKey>(capacity: remaining, comparer: comparer);
            elementCounts.Increment(elements.Current);
            while (elements.MoveNext())
            {
                if (--remaining < 0)
                {
                    // too many elements
                    return null;
                }
                elementCounts.Increment(elements.Current);
            }

            if (remaining > 0)
            {
                // too few elements
                return null;
            }

            return elementCounts;
        }

        /// <summary>
        /// Key Lookup Reduction optimization: this custom datastructure halves the number of <see cref="IEqualityComparer{T}.GetHashCode(T)"/>
        /// and <see cref="IEqualityComparer{T}.Equals(T, T)"/> operations by building in the increment/decrement operations of a counting dictionary.
        /// This also solves <see cref="Dictionary{TKey, TValue}"/>'s issues with null keys
        /// </summary>
        private sealed class CountingSet<T>
        {
            // picked based on observing unit test performance
            private const int MaxLoadPct = 62;
            private const int ItemDistanceThresholdPct = 220;
            private const int DefaultTableSizeIndex = 4; // 389
            private const int MaxHashTableSize = int.MaxValue;

            private readonly IEqualityComparer<T> comparer;
            private Bucket[] buckets;
            private int populatedBucketCount;
            /// <summary>
            /// When we reach this count, we need to resize
            /// </summary>
            private int nextResizeCount;

            private int totalItemDistance;
            private int totalItemCount;

            private int tableSizeIndex;

            public CountingSet(IEqualityComparer<T> comparer, int capacity = int.MinValue)
            {
                this.comparer = comparer;
                if (capacity < 0)
                {
                    this.tableSizeIndex = DefaultTableSizeIndex;
                }
                else
                {
                    this.tableSizeIndex = GetTableSizeIndex(100 * capacity / MaxLoadPct - 1);
                }

                // we pick the initial length by assuming our current table is one short of the desired
                // capacity and then using our standard logic of picking the next valid table size
                this.buckets = new Bucket[HashTableSizes[this.tableSizeIndex]];
                this.nextResizeCount = this.CalculateNextResizeCount();

                this.totalItemCount = 0;
                this.totalItemDistance = 0;
            }

            public bool IsEmpty
            {
                get
                {
#if DEBUG
                    if (this.populatedBucketCount == 0 && (this.totalItemCount != 0 || this.totalItemDistance != 0) ||
                        this.populatedBucketCount != 0 && (this.totalItemCount == 0 || this.totalItemDistance == 0) ||
                        this.populatedBucketCount < 0 || this.totalItemCount < 0 || this.totalItemDistance < 0)
                    {
                        Debug.Assert(false,"Inconsistent count values");
                    }
#endif
                    return this.populatedBucketCount == 0;
                }
            }

            public void Increment(T item)
            {
                // we convert the raw hash code to a uint to get correctly-signed mod operations
                // and get rid of the zero value so that we can use 0 to mean "unoccupied"
                var rawHashCode = this.comparer.GetHashCode(item);
                uint hashCode = rawHashCode == 0 ? uint.MaxValue : unchecked((uint)rawHashCode);

                var bucketIndex = hashCode < this.buckets.Length ? (int)hashCode : (int)(hashCode % this.buckets.Length);
                var swapIndex = bucketIndex;
                var bucketDistance = 1;
                bool found = false;

                while (true) // guaranteed to terminate because of how we set load factor
                {
                    ref var bucket = ref this.buckets[bucketIndex];

                    if (bucket.HashCode == 0)
                    {
                        bucket.Count = 1;
                        bucket.HashCode = hashCode;
                        bucket.Value = item;
                        ++this.populatedBucketCount;

                        ++this.totalItemCount;
                        this.totalItemDistance += bucketDistance;

                        // resize the table if we've grown too full
                        if (this.populatedBucketCount == this.nextResizeCount ||
                            // Or if specific areas of the hash table gets to crowded resulting in an average item distance to grow too high
                            this.totalItemDistance * 100 >= ItemDistanceThresholdPct * this.totalItemCount &&
                            this.populatedBucketCount * 10 > this.buckets.Length  /* put an upper limit to the size of the bucket array */
                        )
                        {
                            Rehash();
                        }

                        // Count sorting is not needed for new buckets. Then should be at the furthest distance. So just return
                        return;
                    }

                    if (bucket.HashCode == hashCode && this.comparer.Equals(bucket.Value, item))
                    {
                        ++bucket.Count;

                        ++this.totalItemCount;
                        this.totalItemDistance += bucketDistance;

                        if (swapIndex == bucketIndex)
                        {
                            // For the common case, fast return
                            return;
                        }
                    
                        found = true;
                    }

                    if (swapIndex != bucketIndex)
                    {
                        Swap(bucketIndex, ref swapIndex, ref bucket);
                    }

                    if (found)
                    {
                        return;
                    }

                    // otherwise march on to the next adjacent bucket
                    if (++bucketIndex == this.buckets.Length)
                    {
                        bucketIndex = 0;
                    }
                    bucketDistance++;
                }
            }

            private void Swap(int bucketIndex, ref int swapIndex, ref Bucket bucket)
            {
                var count = this.buckets[swapIndex].Count - bucket.Count;

                switch (Math.Sign(count))
                {
                    case 1:
                        // Make the current bucket index the new candidate for a swap, because the bucket with the lowest count is the best candidate
                        swapIndex = bucketIndex;
                        break;
                    case -1:
                        var startIndex = bucket.HashCode < this.buckets.Length ? (int)bucket.HashCode : (int)(bucket.HashCode % this.buckets.Length) /* start index*/;

                        if (startIndex == bucketIndex)
                        {
                            // For the common case, fast break
                            break;
                        }

                        int deltaDistance;
                        /* old distance */
                        if (bucketIndex > startIndex)
                        {
                            deltaDistance = bucketIndex - startIndex;
                        }
                        else
                        {
                            deltaDistance = bucketIndex - startIndex + this.buckets.Length;
                        }

                        if (swapIndex != startIndex)
                        {
                            /* new distance */
                            // if swapIndex == startIndex then new distance is 0
                            if (swapIndex > startIndex)
                            {
                                deltaDistance -= swapIndex - startIndex;
                            }
                            else
                            {
                                deltaDistance -= swapIndex - startIndex + this.buckets.Length;
                            }
                        }

                        if (deltaDistance > 0)
                        {
                            this.totalItemDistance += deltaDistance * count;

                            ref var swapBucket = ref this.buckets[swapIndex];

                            // Swap 2 buckets if the distance from the hash entry of the item to the swapIndex is shorter
                            // than the distance to the bucketIndex
                            // Reindexing only ensures partial count sorting - a rehash ensures full count sorting

                            var tmpBucket = bucket;
                            bucket = swapBucket;
                            swapBucket = tmpBucket;

                            // Set swap candidate to the next bucket. It most likely will have a lower count then the bucket we swapped 
                            if (++swapIndex == this.buckets.Length)
                            {
                                swapIndex = 0;
                            }
                        }
                        break;
                }
            }

            private void Rehash()
            {
                if (this.buckets.Length >= MaxHashTableSize)
                {
                    if (this.populatedBucketCount >= MaxHashTableSize)
                    {
                        throw new InvalidOperationException("Hash table is full and cannot grow further!");
                    }

                    // Array cannot be resized further
                    return;
                }

                var newBuckets = new Bucket[HashTableSizes[++this.tableSizeIndex]];

                this.totalItemDistance = 0;

                // rehash
                for (var i = 0; i < this.buckets.Length; ++i)
                {
                    ref var oldBucket = ref this.buckets[i];

                    if (oldBucket.HashCode != 0)
                    {
                        var newBucketIndex = oldBucket.HashCode < newBuckets.Length ? (int)oldBucket.HashCode : (int)(oldBucket.HashCode % newBuckets.Length);
                        var bucketDistance = oldBucket.Count;

                        while (true)
                        {
                            ref var newBucket = ref newBuckets[newBucketIndex];

                            if (newBucket.HashCode == 0)
                            {
                                this.totalItemDistance += bucketDistance;

                                newBucket = oldBucket;
                                break;
                            }
                            else if (newBucket.Count < oldBucket.Count)
                            {
                                this.totalItemDistance += bucketDistance;

                                // Use count sorting to ensure shortest distance to the most frequently accessed buckets
                                var tmpBucket = newBucket;
                                newBucket = oldBucket;
                                oldBucket = tmpBucket;

                                /* Only add the missing distance when inserting the swapped bucket */
                                bucketDistance = 0;
                            }

                            if (++newBucketIndex == newBuckets.Length)
                            {
                                newBucketIndex = 0;
                            }
                            bucketDistance += oldBucket.Count;
                        }
                    }
                }

                this.buckets = newBuckets;
                this.nextResizeCount = this.CalculateNextResizeCount();
            }

            public int Decrement(T item)
            {
                // we convert the raw hash code to a uint to get correctly-signed mod operations
                // and get rid of the zero value so that we can use 0 to mean "unoccupied"
                var rawHashCode = this.comparer.GetHashCode(item);
                uint hashCode = rawHashCode == 0 ? uint.MaxValue : unchecked((uint)rawHashCode);

#if DEBUG
                var startIndex = (int)(hashCode % this.buckets.Length);
                var offset = this.buckets.Length - startIndex;
                var bucketIndex = startIndex;
#else
                var bucketIndex = hashCode < this.buckets.Length ? (int)hashCode : (int)(hashCode % this.buckets.Length);
#endif
                while (true) // guaranteed to terminate because of how we set load factor
                {
                    ref var bucket = ref this.buckets[bucketIndex];

                    if (bucket.HashCode == 0)
                    {
                        return -1;
                    }

                    if (bucket.HashCode == hashCode && this.comparer.Equals(bucket.Value, item))
                    {
                        if (--bucket.Count == 0)
                        {
                            // Note: we can't do this because it messes up our try-find logic
                            //// mark as unpopulated. Not strictly necessary because CollectionEquals always does all increments
                            //// before all decrements currently. However, this is very cheap to do and allowing the collection to
                            //// "just work" in any situation is a nice benefit
                            //// this.buckets[bucketIndex].HashCode = 0;

                            --this.populatedBucketCount;
                        }

#if DEBUG
                        --this.totalItemCount;
                        this.totalItemDistance -= ((bucketIndex + offset) % this.buckets.Length) + 1;
#endif

                        return bucket.Count;
                    }

                    // otherwise march on to the next adjacent bucket
                    if (++bucketIndex == this.buckets.Length)
                    {
                        bucketIndex = 0;
                    }
                }
            }

            private int CalculateNextResizeCount()
            {
                return this.buckets.Length >= MaxHashTableSize ? MaxHashTableSize : MaxLoadPct * this.buckets.Length / 100 + 1;
            }

            private static readonly int[] HashTableSizes = new[]
            {
                // hash table primes from http://planetmath.org/goodhashtableprimes
                23, 53, 97, 193, 389, 769, 1543, 3079, 6151, 12289,
                24593, 49157, 98317, 196613, 393241, 786433, 1572869,
                3145739, 6291469, 12582917, 25165843, 50331653, 100663319,
                201326611, 402653189, 805306457, 1610612741,
                // the first two values are (1) a prime roughly half way between the previous value and int.MaxValue
                // and (2) the prime closest too, but not above, int.MaxValue. The maximum size is, of course, int.MaxValue
                1879048201, 2147483629, int.MaxValue
            };

            private static int GetTableSizeIndex(int currentSize)
            {
                for (var i = 0; i < HashTableSizes.Length; ++i)
                {
                    var nextSize = HashTableSizes[i];
                    if (nextSize > currentSize)
                    { return i; }
                }

                return HashTableSizes.Length - 1;
            }

            [DebuggerDisplay("{Value}, {Count}, {HashCode}")]
            private struct Bucket
            {
                // note: 0 (default) means the bucket is unoccupied
                internal uint HashCode;
                internal T Value;
                internal int Count;
            }
        }
#endregion

#region ---- GetOrAdd ----
        /// <summary>
        /// If <paramref name="key"/> exists in <paramref name="dictionary"/>, returns the associated value. Otherwise,
        /// generates a new value by applying <paramref name="valueFactory"/> to the given <paramref name="key"/>. The 
        /// new value is stored in <paramref name="dictionary"/> and returned
        /// </summary>
        public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> valueFactory)
        {
            if (dictionary == null) { throw new ArgumentNullException(nameof(dictionary)); }
            if (valueFactory == null) { throw new ArgumentNullException(nameof(valueFactory)); }

            TValue existing;
            if (dictionary.TryGetValue(key, out existing))
            {
                return existing;
            }

            var value = valueFactory(key);
            dictionary.Add(key, value);
            return value;
        }
#endregion

        private static bool TryFastCount<T>(IEnumerable<T> @this, out int count)
        {
            var collection = @this as ICollection<T>;
            if (collection != null)
            {
                count = collection.Count;
                return true;
            }

            var readOnlyCollection = @this as IReadOnlyCollection<T>;
            if (readOnlyCollection != null)
            {
                count = readOnlyCollection.Count;
                return true;
            }

            count = -1;
            return false;
        }
    }
}
