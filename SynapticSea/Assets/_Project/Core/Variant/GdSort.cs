using System;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Faithful port of Godot 4.7 <c>core/templates/sort_array.h</c> (introsort + final insertion sort).
    /// It is NOT stable; using it keeps tie ordering identical to GDScript <c>Array.sort()</c> /
    /// <c>sort_custom()</c>, which procgen and loot determinism depend on.
    /// </summary>
    public static class GdSort
    {
        const int IntrosortThreshold = 16;

        public static void SortCustom<T>(IList<T> list, Func<T, T, bool> less)
        {
            if (list == null || list.Count < 2) return;
            var s = new Sorter<T>(list, less);
            s.SortRange(0, list.Count);
        }

        /// <summary>GDScript <c>Array.sort()</c> with Godot's default Variant ordering.</summary>
        public static void Sort(IList<object> list) => SortCustom(list, V.VariantLess);

        struct Sorter<T>
        {
            readonly IList<T> _a;
            readonly Func<T, T, bool> _less;

            public Sorter(IList<T> a, Func<T, T, bool> less)
            {
                _a = a;
                _less = less;
            }

            bool Compare(T x, T y) => _less(x, y);

            int MedianOf3Index(int ai, int bi, int ci)
            {
                T a = _a[ai], b = _a[bi], c = _a[ci];
                if (Compare(a, b))
                {
                    if (Compare(b, c)) return bi;
                    if (Compare(a, c)) return ci;
                    return ai;
                }
                if (Compare(a, c)) return ai;
                if (Compare(b, c)) return ci;
                return bi;
            }

            static int Bitlog(int n)
            {
                int k;
                for (k = 0; n != 1; n >>= 1) ++k;
                return k;
            }

            void PushHeap(int first, int holeIdx, int topIndex, T value)
            {
                int parent = (holeIdx - 1) / 2;
                while (holeIdx > topIndex && Compare(_a[first + parent], value))
                {
                    _a[first + holeIdx] = _a[first + parent];
                    holeIdx = parent;
                    parent = (holeIdx - 1) / 2;
                }
                _a[first + holeIdx] = value;
            }

            void PopHeap(int first, int last, int result, T value)
            {
                _a[result] = _a[first];
                AdjustHeap(first, 0, last - first, value);
            }

            void PopHeap(int first, int last) => PopHeap(first, last - 1, last - 1, _a[last - 1]);

            void AdjustHeap(int first, int holeIdx, int len, T value)
            {
                int topIndex = holeIdx;
                int secondChild = 2 * holeIdx + 2;
                while (secondChild < len)
                {
                    if (Compare(_a[first + secondChild], _a[first + (secondChild - 1)])) secondChild--;
                    _a[first + holeIdx] = _a[first + secondChild];
                    holeIdx = secondChild;
                    secondChild = 2 * (secondChild + 1);
                }
                if (secondChild == len)
                {
                    _a[first + holeIdx] = _a[first + (secondChild - 1)];
                    holeIdx = secondChild - 1;
                }
                PushHeap(first, holeIdx, topIndex, value);
            }

            void SortHeap(int first, int last)
            {
                while (last - first > 1) PopHeap(first, last--);
            }

            void MakeHeap(int first, int last)
            {
                if (last - first < 2) return;
                int len = last - first;
                int parent = (len - 2) / 2;
                while (true)
                {
                    AdjustHeap(first, parent, len, _a[first + parent]);
                    if (parent == 0) return;
                    parent--;
                }
            }

            void PartialSort(int first, int last, int middle)
            {
                MakeHeap(first, middle);
                for (int i = middle; i < last; i++)
                    if (Compare(_a[i], _a[first])) PopHeap(first, middle, i, _a[i]);
                SortHeap(first, middle);
            }

            int Partitioner(int first, int last, int pivot)
            {
                // Godot tracks the pivot by address; when the pivot slot is swapped, the tracked slot follows.
                int pivotLoc = pivot;
                while (true)
                {
                    while (first != pivot && Compare(_a[first], _a[pivotLoc])) first++;
                    last--;
                    while (last != pivot && Compare(_a[pivotLoc], _a[last])) last--;
                    if (first >= last) return first;
                    if (pivotLoc == first) pivotLoc = last;
                    else if (pivotLoc == last) pivotLoc = first;
                    T tmp = _a[first];
                    _a[first] = _a[last];
                    _a[last] = tmp;
                    first++;
                }
            }

            void Introsort(int first, int last, int maxDepth)
            {
                while (last - first > IntrosortThreshold)
                {
                    if (maxDepth == 0)
                    {
                        PartialSort(first, last, last);
                        return;
                    }
                    maxDepth--;
                    int cut = Partitioner(first, last, MedianOf3Index(first, first + (last - first) / 2, last - 1));
                    Introsort(cut, last, maxDepth);
                    last = cut;
                }
            }

            void UnguardedLinearInsert(int last, T value)
            {
                int next = last - 1;
                while (Compare(value, _a[next]))
                {
                    _a[last] = _a[next];
                    last = next;
                    next--;
                }
                _a[last] = value;
            }

            void LinearInsert(int first, int last)
            {
                T val = _a[last];
                if (Compare(val, _a[first]))
                {
                    for (int i = last; i > first; i--) _a[i] = _a[i - 1];
                    _a[first] = val;
                }
                else
                {
                    UnguardedLinearInsert(last, val);
                }
            }

            void InsertionSort(int first, int last)
            {
                if (first == last) return;
                for (int i = first + 1; i != last; i++) LinearInsert(first, i);
            }

            void UnguardedInsertionSort(int first, int last)
            {
                for (int i = first; i != last; i++) UnguardedLinearInsert(i, _a[i]);
            }

            void FinalInsertionSort(int first, int last)
            {
                if (last - first > IntrosortThreshold)
                {
                    InsertionSort(first, first + IntrosortThreshold);
                    UnguardedInsertionSort(first + IntrosortThreshold, last);
                }
                else
                {
                    InsertionSort(first, last);
                }
            }

            public void SortRange(int first, int last)
            {
                if (first == last) return;
                Introsort(first, last, Bitlog(last - first) * 2);
                FinalInsertionSort(first, last);
            }
        }
    }
}
