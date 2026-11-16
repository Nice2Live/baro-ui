using System;
using System.Collections.Generic;

namespace BaroUI.Core
{
    /// <summary>An owner of textures the ledger may take back: it drops its entry for the token (the caller frees the texture through the graveyard).
    /// The ledger has already forgotten the token when Release is called, so the owner must not call Remove for it.</summary>
    public interface ITextureOwner
    {
        void Release(int token);
    }

    /// <summary>One texture budget for everything a window keeps as a texture (CSS pictures, canvas pictures, shadow tiles, gradient ramps and areas, corner
    /// masks and tiles): the total bytes and the number of entries. Over either limit <see cref="Trim"/> releases the entries nobody used in this frame or
    /// the one before, the longest unused first, in one pass (one sort), down to three quarters of both limits. An entry used in this frame or the last one
    /// is never released (a batch may still hold its texture). Pure bookkeeping: the textures stay in their owners (Render/TextureCache.cs).</summary>
    public sealed class TextureLedger
    {
        private struct Record
        {
            public ITextureOwner Owner;
            public long Bytes, Frame;
            public bool Live;
        }

        private readonly List<Record> records = new();
        private readonly Stack<int> free = new();
        private readonly List<int> order = new();

        public TextureLedger(long maxBytes, int maxCount)
        {
            MaxBytes = maxBytes;
            MaxCount = maxCount;
        }

        /// <summary>The most bytes (RGBA) kept by every owner together.</summary>
        public long MaxBytes { get; }
        /// <summary>The most entries kept by every owner together.</summary>
        public int MaxCount { get; }
        /// <summary>The frame counter: the painter calls <see cref="BeginFrame"/> once per paint.</summary>
        public long Frame { get; private set; }
        public long Bytes { get; private set; }
        public int Count { get; private set; }

        /// <summary>A new frame: what is used from now on counts as used in this frame.</summary>
        public void BeginFrame() => Frame++;

        /// <summary>True when the budget is passed (Trim would release something if anything is idle).</summary>
        public bool Over => Bytes > MaxBytes || Count > MaxCount;

        /// <summary>Takes an entry of this many bytes for an owner; the returned token is the owner's key for it (Touch, Remove, Release).
        /// It counts as used in this frame.</summary>
        public int Add(ITextureOwner owner, long bytes)
        {
            int id;
            if (free.Count > 0) { id = free.Pop(); }
            else { id = records.Count; records.Add(default); }
            records[id] = new Record { Owner = owner, Bytes = bytes, Frame = Frame, Live = true };
            Bytes += bytes;
            Count++;
            return id;
        }

        /// <summary>The entry was used: it counts as used in this frame.</summary>
        public void Touch(int token)
        {
            var r = records[token];
            if (r.Live) { r.Frame = Frame; records[token] = r; }
        }

        /// <summary>The owner let the entry go itself (replaced, cleared): the budget forgets it. No Release call.</summary>
        public void Remove(int token)
        {
            var r = records[token];
            if (!r.Live) { return; }
            Bytes -= r.Bytes;
            Count--;
            records[token] = new Record { Owner = null, Live = false };
            free.Push(token);
        }

        /// <summary>Over a limit: releases the idle entries, longest unused first, until both totals are at most three quarters of their limits. Returns how many
        /// were released. Nothing happens while the budget is not passed.</summary>
        public int Trim()
        {
            if (!Over) { return 0; }
            long lowBytes = MaxBytes / 4 * 3;
            int lowCount = MaxCount / 4 * 3;
            order.Clear();
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Live && r.Frame < Frame - 1) { order.Add(i); }
            }
            order.Sort((a, b) => records[a].Frame.CompareTo(records[b].Frame));
            int released = 0;
            foreach (int id in order)
            {
                if (Bytes <= lowBytes && Count <= lowCount) { break; }
                var r = records[id];
                Bytes -= r.Bytes;
                Count--;
                var owner = r.Owner;
                records[id] = new Record { Owner = null, Live = false };
                free.Push(id);
                released++;
                owner.Release(id);
            }
            order.Clear();
            return released;
        }
    }
}
