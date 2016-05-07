﻿namespace FSharp.Object.Diff

open System
open System.Collections.Generic

type Accessor =
  abstract member ElementSelector: ElementSelector
  abstract member Get: obj -> obj
  abstract member Set: obj * obj -> unit
  abstract member Unset: obj -> unit

type RootAccessor = RootAccessor
with
  override __.ToString() = "root element"
  interface Accessor with
    member __.ElementSelector = RootElementSelector.Instance
    member __.Get(target) = target
    member __.Set(_, _) = raise <| NotSupportedException()
    member __.Unset(_) = raise <| NotSupportedException()

type TypeAwareAccessor =
  inherit Accessor
  abstract member Type: Type

type CategoryAware =
  abstract member CategoriesFromAttribute: Set<string>

type PropertyAwareAccessor =
  inherit TypeAwareAccessor
  inherit CategoryAware
  abstract member PropertyName: string
  abstract member PropertyAttributes: Attribute seq
  abstract member GetPropertyAttribute<'T when 'T :> Attribute and 'T : null> : unit -> 'T

type CollectionItemAccessor(referenceItem: obj, identityStrategy: IdentityStrategy) =

  let objectAsCollection: obj -> ICollection<obj> = function
  | null -> null
  | :? ICollection<obj> as c -> c
  | o -> raise <| ArgumentException(o.GetType().FullName)

  let remove (xs: ICollection<_>) =
    let rec inner index =
      if index >= xs.Count then ()
      else
        let x = Seq.nth index xs
        if x <> null && identityStrategy.Equals(x, referenceItem) then
          xs.Remove(x) |> ignore
        else inner (index + 1)
    inner 0

  new(referenceItem) = CollectionItemAccessor(referenceItem, EqualsIdentityStrategy :> IdentityStrategy)

  member __.ElementSelector =
    let selector = CollectionItemElementSelector(referenceItem)
    if identityStrategy = null then selector else selector.WithIdentityStrategy(identityStrategy)
    :> ElementSelector

  member internal __.TryGet(target) =
    objectAsCollection target
    |> Seq.tryFind (fun item -> item <> null && identityStrategy.Equals(item, referenceItem))

  member this.Get(target: obj) =
    match this.TryGet(target) with
    | Some o -> o
    | None -> null

  member __.Unset(target: obj) =
    let targetCollection = objectAsCollection target
    if targetCollection <> null then
      remove targetCollection

  member this.Set(target: obj, value: obj) =
    let targetCollection = objectAsCollection target
    if targetCollection <> null then
      let previous = this.Get(target)
      if previous <> null then this.Unset(target)
      targetCollection.Add(value)

  override this.ToString() =
    "collection item " + this.ElementSelector.ToString()

  member __.Type = if referenceItem <> null then referenceItem.GetType() else null

  interface TypeAwareAccessor with
    member this.Type = this.Type
    member this.ElementSelector = this.ElementSelector
    member this.Get(target) = this.Get(target)
    member this.Set(target, value) = this.Set(target, value)
    member this.Unset(target) = this.Unset(target)

type MapEntryAccessor(referenceKey: obj) =

  let objectToDictionary: obj -> Dictionary<obj, obj> = function
  | null -> null
  | :? Dictionary<obj, obj> as d -> d
  | o -> raise <| ArgumentException(o.GetType().FullName)

  member __.ElementSelector = MapKeyElementSelector(referenceKey) :> ElementSelector

  override this.ToString() =
    "map key " + this.ElementSelector.ToString()

  member __.GetKey(target: Dictionary<obj, obj>) =
    if target = null then null
    else
      match target.Keys |> Seq.tryFind ((=) referenceKey) with
      | Some k -> k
      | None -> null

  member __.Get(target: obj) =
    let target = objectToDictionary target
    if target <> null then
      target.[referenceKey]
    else null

  member __.Set(target: obj, value: obj) =
    let target = objectToDictionary target
    if target <> null then
      if target.ContainsKey(referenceKey) then target.Remove(referenceKey) |> ignore
      target.Add(referenceKey, value)

  member __.Unset(target: obj) =
    let target = objectToDictionary target
    if target <> null then
      target.Remove(referenceKey) |> ignore

  interface Accessor with
    member this.ElementSelector = this.ElementSelector
    member this.Get(target) = this.Get(target)
    member this.Set(target, value) = this.Set(target, value)
    member this.Unset(target) = this.Unset(target)

type Instances(sourceAccessor: Accessor, working: obj, base_: obj, fresh: obj) =

  static member Of(sourceAccessor: Accessor, working: 'T, base_: 'T, fresh: 'T) =
    Instances(sourceAccessor, working, base_, fresh)

  static member Of(sourceAccessor: Accessor, working: 'T, base_: 'T) =
    let fresh = if working <> null then Type.FreshInstanceOf<'T>() else null
    Instances(sourceAccessor, working, base_, fresh)

  static member Of(working: 'T, base_: 'T) =
    let fresh =
      match box working with
      | null -> null
      | _ -> Type.FreshInstanceOf<'T>()
    Instances(RootAccessor, working, base_, fresh)

  member __.SourceAccessor = sourceAccessor

  member __.Access(accessor: Accessor) =
    Instances(accessor, accessor.Get(working), accessor.Get(base_), accessor.Get(fresh))

  member __.Working = working
  member __.TryGetWorking<'T>() = if working <> null then Some(working :?> 'T) else None
  member __.Base = base_
  member __.TryGetBase<'T>() = if base_ <> null then Some(base_ :?> 'T) else None
  member this.Fresh: obj =
    if fresh = null then
      if Type.isPrimitive this.Type then Activator.CreateInstance(this.Type)
      else fresh
    else
      fresh
  member this.GetFresh(typ: Type) =
    let o = this.Fresh
    if o <> null then Convert.ChangeType(o, typ)
    else null

  member __.TryToGetTypeFromSourceAccessor() =
    match sourceAccessor with
    | :? TypeAwareAccessor as accessor -> accessor.Type
    | _ -> null

  member __.AreEqual = obj.IsEqual(base_, working)
  member __.AreSame = Object.ReferenceEquals(working, base_)
  member __.AreNull = working = null && base_ = null

  member this.Type =
    let types = Type.TypesOf(working, base_, fresh)
    let raise () =
      types
      |> sprintf "Detected instances of different types %A. Instances must either be null or have the exact same type."
      |> ArgumentException
      |> raise
    let sourceAccessorType = this.TryToGetTypeFromSourceAccessor()
    if Type.isPrimitive sourceAccessorType then sourceAccessorType
    elif Seq.isEmpty types then null
    elif Seq.length types = 1 then
      types |> Seq.head
    elif Seq.length types > 1 then
      if typeof<System.Collections.IDictionary>.AllAssignableFrom(types) then
        typeof<System.Collections.IDictionary>
      elif typeof<System.Collections.IEnumerable>.AllAssignableFrom(types) then
        typeof<System.Collections.IEnumerable>
      else
        // TODO: implement
        raise ()
    else raise ()

  member this.IsPrimitiveType = this.Type <> null && this.Type.IsPrimitive

  member this.HasBeenAdded =
    if working <> null && base_ = null then true
    elif this.IsPrimitiveType && Object.IsEqual(this.Fresh, base_) && (not <| Object.IsEqual(base_, working)) then true
    else false

  member this.HasBeenRemoved =
    if base_ <> null && working = null then true
    elif this.IsPrimitiveType && Object.IsEqual(this.Fresh, working) && (not <| Object.IsEqual(base_, working)) then true
    else false
