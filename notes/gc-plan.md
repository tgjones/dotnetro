# Garbage collection plan

Goal: let a program reclaim memory it allocated during initialisation (level load, setup) and no longer needs, by calling
`GC.Collect()` at a point of its choosing. Programs that never allocate in their main loop never pay a collection pause.

## Decisions (agreed)

- **Opt-in, explicit.** Collection happens only inside a `GC.Collect()` call. `ManagedHeap.Alloc` never starts a
  collection by itself. (Open question below: whether `Alloc` may collect as a last resort before halting on exhaustion.)
- **Pause timing is a non-issue by construction.** A frame that does not call `Alloc` or `GC.Collect` cannot be delayed by
  the collector. The costs that remain are code size, per-object overhead and fragmentation, not frame timing.
- **Non-moving mark-sweep.** No compaction: compaction needs precise roots, and we do not have stack maps.
- **Precise heap, conservative roots.** Inside heap objects we know exactly which words are references (a per-type
  reference map). The roots (static storage, zero page, soft stack) are scanned conservatively.
- **Not a general-purpose GC.** No finalizers, weak references, generations, or concurrent collection.

## What exists today (so this plan needs no re-exploration)

- `src/DotNetro.CoreLib/Runtime/ManagedHeap.cs`: bump allocator, `s_heapPtr = 0x1000`, never freed, **no end-of-heap
  check**. `Alloc(size, vtablePtr)` writes the 2-byte vtable pointer, zeroes the contents, and returns a pointer to the
  contents (the vtable word is at `obj-2`). `AllocArray(length, contentSize)` calls `Alloc(2 + contentSize, null)`, stores
  the u16 length at the returned pointer, and returns that pointer (so an array reference points at the length word and
  `obj-2` is a null vtable word).
- **Strings and arrays both have a null vtable word** and are not distinguished from each other by anything in the object.
  String constants are *not* heap objects: `RegisterStringGlobal` emits them as module data (`string{n:X4}`), so a
  string reference can point outside the heap. The mark phase must ignore pointers outside the heap range.
- Vtables are globals `Vtable_<type>`: `[base vtable ptr][slot 0][slot 1]…`. `VtableHeaderSize` (`IlToMirTranslator.cs`,
  ~line 319) is used at ~369 (global size) and ~2521 (callvirt slot address); it is the only thing that fixes the
  header size, so changing it is a two-site edit plus the emitter at `RegisterVtableGlobals` (~336).
  `TypeCheck.IsInstance` walks word 0 (the base chain).
- Allocation sites in the translator: `newobj` (~2554, `size = type.InstanceSize`), box (~2610), `newarr` (~2750,
  `ElementSize`). Static fields are zero-initialised `.bss`-style `MirGlobal`s named `<Owner>_<Field>`
  (`RegisterStaticFieldGlobal`). `InstanceSize` for a class already includes its inherited fields.
- Locals/spills of **non-reentrant** functions live in static frames: zero page `$70–$8F` on BBC Micro
  (`MOS6502FramePlacementPass`, `StaticFrameColorer`) or absolute `.bss` slots (the overflow, and the base-chip target).
  **Reentrant** functions (`ReentrancyAnalysis`) use a soft stack addressed through RC0/RC1 (CC_MOS). Imaginary registers
  `$zp0…` are zero-page bytes. The calling convention is caller-saves, so at a `GC.Collect()` call every live reference in a
  caller is in one of those places, not in A/X/Y.
- `DefaultOrigin` for BBC Micro is `0x2000` while the heap starts at `0x1000`: **that is only 4 KB before the heap would
  run into the program.** Phase 0 resolves this.

## Design

### Block header

Every heap allocation gets a 2-byte **block word** in front of the existing vtable word:

```
obj-4  block word   bit 15 = mark, bit 14 = "no references inside", bits 0–13 = total block size in bytes
obj-2  vtable word  (unchanged: vtable pointer, or 0 for strings/arrays)
obj    contents
```

- A free block uses the same layout; its first content word is the next-free pointer. The heap is walkable linearly
  from `heapStart` to `s_heapPtr` by adding the size from each block word, which is what sweep and the verifier use.
- 14 bits limits one block to 16 KB − 1. Fine for a 6502 heap; `Alloc` halts on a larger request.
- The "no references" bit is how `byte[]`, `char[]`, `string` and other primitive arrays skip scanning. `AllocArray`
  gains a third parameter (`elementsHaveReferences`) that the translator sets for `newarr` of a reference type or of a
  struct containing references.
- Cost: 2 bytes per object. Existing translator code does not change because the object reference still points at the
  contents and the vtable word is still at `obj-2`.

### Per-type reference map

- `VtableHeaderSize` becomes `2 * PointerSize`: `[base vtable][reference-map ptr][slot…]`. Both uses of the constant move
  with it.
- The reference map is a separate read-only global per type: the byte offsets (from the contents pointer) of every
  field that holds a managed reference, including inherited fields and the fields of embedded structs (flattened), ended
  by `0xFF`. A type with no references points at one shared empty map. Instance fields and their offsets are already
  known to the translator for `ldfld`; reuse that source of truth.
- Boxed value types get a vtable too, so a boxed struct is described the same way.

### Roots

The translator/encoder defines symbols the collector uses to find the root ranges:

1. **Static storage:** the span of all `.bss` and initialised data globals (static fields and absolute frame slots).
   Scanning vtables and string constants in the same span is harmless: their words point at code or data, not the heap,
   and are rejected by the range check. Refine to a precise list of reference-typed static fields later if scan cost matters.
2. **Zero page:** the imaginary registers and the frame window `$70–$8F` (target-supplied range).
3. **Soft stack:** from the soft-stack pointer (RC0/RC1) to its base.

Every 2-byte word in a root range (at any alignment) is a candidate reference. A candidate is resolved to the
**block that contains it**, so interior pointers (`ldflda`, `ldelema`, `fixed`) held in roots keep their object alive.
Heap fields never hold interior pointers (managed fields cannot store them), so only root candidates need this
lookup; objects found while marking are always exact.

Resolving a candidate `p`:

1. Reject `p` outside `[heapStart, s_heapPtr)`.
2. Scan the block-start bitmap backwards from `p` to the nearest set bit; that is the block start `b`.
3. Reject if `b` is a free block, or `p` is at or beyond `b + size` (the size is in the block word).
4. Otherwise the object is the block at `b`; mark it.

The backward scan is bounded by the object size (a few bitmap bytes for typical objects), and runs once per root
candidate only.

**The block-start bitmap is required** (formerly "Option A"): 1 bit per 2 heap bytes, maintained by `Alloc` and sweep,
about 6% of heap RAM (1.5 KB for a 24 KB heap). A heuristic validity check cannot resolve interior pointers at all, so it
is no longer an option.

Consequences, accepted:

- More false retention: any root word landing inside a block keeps that block alive (costs memory, not correctness).
- A pointer **one past the end** of an object resolves to the *next* block, not its own. If such a pointer (e.g. the end of
  a pointer loop) is the only reference left to an array, the array can be freed. This is safe as long as the pointer is
  never moved back into the array; compiled C# loops normally keep the array reference live as well. Document it.

### Collection

1. **Mark:** for each accepted root candidate set the mark bit and push it on a fixed-size mark stack (128 entries in
   `.bss`). Pop and scan: objects via the reference map, reference arrays via their length, skipping blocks with the
   "no references" bit. On mark-stack overflow set a flag and continue; after the stack drains, if the flag is set,
   linearly rescan the heap for marked blocks with unmarked children and repeat until a pass makes no change.
2. **Sweep:** walk the heap linearly. Unmarked blocks become free and adjacent free blocks coalesce. Clear mark bits.
   A free run touching `s_heapPtr` lowers the bump pointer instead of joining the free list.
3. **Allocate:** `Alloc` first tries the free list (first fit; split when the remainder is at least a minimal block),
   then the bump pointer, then halts if neither fits (see Phase 0). The existing zeroing loop is kept.

### Public API

`System.GC` in the corelib: `GC.Collect()` and a diagnostic `GC.FreeBytes()` (free-list bytes + bump headroom, used by
tests only). `GC.Collect` is a normal C# method, so it is only compiled into programs that call it (the translator
discovers methods by reachability). The per-object block word and the free-list allocator are paid by every program.

## Phases

### Phase 0 — memory map and exhaustion check (independent value, do first)

- [ ] Decide where the heap lives. Resolve `s_heapPtr = 0x1000` vs `DefaultOrigin = 0x2000`. Likely: heap starts after
      the program's data, ends at a target-supplied limit (below screen memory or `HIMEM`). The limit is a target
      property (BBC mode dependent), exposed as a symbol or constant to `ManagedHeap`.
- [ ] `Alloc` halts (like `TypeCheck.CastClass`'s `while (true)`) when `s_heapPtr + size` exceeds the limit, instead of
      corrupting memory. Revisit when `throw`/exceptions land (`notes/exceptions-plan.md`: `throw` is fatal).
- [ ] Lit test that allocates past the limit and expects the halt marker.

### Phase 1 — block header, free list, no collection

- [ ] Block word, size recorded in `Alloc`/`AllocArray`, `AllocArray` third parameter, translator sets it at `newarr`.
- [ ] Free-list allocate path (empty until Phase 3). Heap walker used by a debug check.
- [ ] Block-start bitmap maintained by `Alloc`, with a backward-scan "containing block" lookup.
- [ ] Existing lit suite (`dotnet test --project src/DotNetro.Compiler.Tests`) still passes unchanged. Record the
      code-size and per-object overhead delta against the Phase 0 baseline in this file.

### Phase 2 — type metadata

- [ ] Vtable header grows to two words; `VtableHeaderSize` and its two uses updated; `TypeCheck` unaffected (word 0).
- [ ] Reference-map globals emitted in `RegisterVtableGlobals` for every type in `_vtableTypes` that has instances.
- [ ] `src/Irie.Tests` or `DotNetro.Compiler.Tests` golden for the emitted map of a small class hierarchy with a struct field.

### Phase 3 — collector

- [ ] Root-range symbols (static span, zero-page range, soft stack bounds) from the translator/target.
- [ ] `DotNetro.Runtime.GarbageCollector` in C#: mark with overflow rescan, sweep with coalescing, bump-pointer lowering.
- [ ] `System.GC` with `Collect()` and `FreeBytes()`.
- [ ] Lit tests (each is `DIFF`ed against the host runtime, so print only values that match on both, using `GC.FreeBytes()` only in
      an emulator-only comparison like `FreeBytes() > N`, or a host stub):
  - garbage in a loop exceeding total heap size, with `GC.Collect()` between rounds (would halt without a collector);
  - an object kept alive by a static field, a local, an array of references, a linked list, a boxed value, a string;
  - a cycle, and a long list (forces mark-stack overflow);
  - reuse of freed blocks: allocate, drop, collect, allocate a differently sized object;
  - an interior pointer (`fixed` over an array element, `ldflda` of a field) as the only root across `GC.Collect()`;
  - reentrant function holding the only reference on the soft stack while it calls `GC.Collect()`.

### Phase 4 — measure and decide on extras

- [ ] Record bytes of code and RAM overhead for hello-world and for a program using `GC.Collect()`; put the numbers here.
- [ ] Decide: precise static-field root list, 4-byte alignment to shrink the bitmap, a build switch for programs that want the old bump allocator.

## Open questions

1. May `Alloc` run a collection as a last resort before halting on exhaustion? It is cheap to add and only runs when the
   program would otherwise die, but it makes collection implicit. Default stays **no** (explicit only) unless you decide otherwise.
2. Bitmap granularity: 2-byte (6% of heap) vs 4-byte aligned allocation (3%, wastes about 1.5 bytes per object).
3. Is the 2-byte per-object block word acceptable for programs that never call `GC.Collect()`? If not, a build switch
   keeps the Phase 0 bump allocator.

## Risks

- Conservative roots can retain garbage through stale frame contents. Acceptable: it costs memory, not correctness.
- A one-past-the-end pointer is the only reference that does not keep its object alive (see Roots); a program that
  moves such a pointer back into the array after a `GC.Collect()` with no other reference to the array would use freed memory.
- Non-moving: fragmentation can leave enough total free bytes but no block large enough.
- Code size of the allocator and collector on a 32 KB machine is unmeasured until Phase 4.
