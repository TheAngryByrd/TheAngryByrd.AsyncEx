module Tests

open System
open Expecto
open TheAngryByrd.AsyncEx
open TheAngryByrd.AsyncEx.Say
open System.Threading
open System.Threading.Tasks
// open FSharp.Control.Tasks.CopiedDoNotReference.V2.ContextInsensitive
open TAB.FSharp.Control.Tasks.V2.ContextInsensitive
open FsToolkit.ErrorHandling

type CancellableTask<'a> = CancellationToken -> Task<'a>


type Async =
    static member WithCancellation(computation : Async<'T>, cancellationToken : CancellationToken) : Async<'T> =
        async {
            let! ct2 = Async.CancellationToken
            use cts = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, ct2)
            let tcs = new TaskCompletionSource<'T>()
            use _reg = cts.Token.Register (fun () -> tcs.TrySetCanceled() |> ignore)
            let inner =
                async {
                    try
                      let! a = computation
                      tcs.TrySetResult a |> ignore
                    with ex ->
                      tcs.TrySetException ex |> ignore
                }
            Async.Start (inner, cts.Token)
            return! Async.AwaitTask tcs.Task
        }
    static member WithCancellation2 (cancellationToken : CancellationToken) computation =
      Async.WithCancellation(computation, cancellationToken)


[<AutoOpen>]
module Extensions =
    type AsyncBuilder with
        member inline __.Bind(t : Task<'a>, cont) = async.Bind(t |> Async.AwaitTask, cont)
        member inline __.Bind(t : Task, cont) = async.Bind(t |> Async.AwaitTask, cont)
        member inline __.ReturnFrom(t : Task<'a>) = async.ReturnFrom(t |> Async.AwaitTask)
        member inline __.ReturnFrom(t : Task) = async.ReturnFrom(t |> Async.AwaitTask)
        member inline __.Bind(t : CancellableTask<'a>, cont : 'a -> Async<'b>) =
            let augmented = async {
                let! ct = Async.CancellationToken
                return! t ct
            }
            async.Bind(augmented, cont)
        member inline __.ReturnFrom(t : CancellableTask<'a>) =
            let augmented = async {
                let! ct = Async.CancellationToken
                return! t ct
            }
            async.ReturnFrom augmented


let ``Bindable CancellableTask`` =
    ftestCaseAsync "Bindable CancellableTask" <| async {
        let mutable wasCalled = false
        let sideEffect (ct : CancellationToken) = task {
            printfn "sideEffect1"
            do! Task.Delay(TimeSpan.FromSeconds(5.), ct)
            printfn "sideEffect2"
            wasCalled <- true
        }

        let inner = async {
            do! sideEffect CancellationToken.None
            Expect.isFalse wasCalled "Side effect should not occur"
        }

        use cts = new CancellationTokenSource ()
        cts.CancelAfter(TimeSpan.FromSeconds(0.1))
        try
            do! Async.WithCancellation2 cts.Token inner
        with :? TaskCanceledException as e ->
            // Cancellation is the point here
            ()
    }

let ``AsyncResult passes along cancellationToken`` =
    testCaseAsync "AsyncResult passes along cancellationToken" <| async {

        let mutable wasCalled = false
        let sideEffect (ct : CancellationToken) = task {
            do! Task.Delay(TimeSpan.FromSeconds(5.), ct)
            wasCalled <- true
        }

        let lowLevel = async {
            do! sideEffect
        }

        let midLevel = asyncResult {
            let! result = lowLevel
            return result
        }

        let topLevel = async {
            return! midLevel
        }

        use cts = new CancellationTokenSource ()
        cts.CancelAfter(TimeSpan.FromSeconds(1.1))
        try
            let! _ = Async.WithCancellation2 cts.Token topLevel
            ()
        with :? TaskCanceledException as e ->
            // Cancellation is the point here
            ()
    }

let ``Foo`` =
    testCaseAsync "Foo" <| async {
        let mutable wasCalled = false
        let sideEffect (ct : CancellationToken) = task {
            printfn "sideeffect1"
            do! Task.Delay(TimeSpan.FromSeconds(5.), ct)
            printfn "sideeffect2"
            wasCalled <- true
        }

        use cts = new CancellationTokenSource ()
        cts.CancelAfter(TimeSpan.FromSeconds(0.1))

        let topLevel = taskCt cts.Token {
            let! r = sideEffect CancellationToken.None
            return r
            // return! sideEffect CancellationToken.None
            // return r
        }

        try
            let! _ = topLevel
            ()
        with :? TaskCanceledException as e ->
            // Cancellation is the point here
            printfn "TaskCanceledException -> %A" e
            ()
        Expect.isFalse wasCalled "Side effect should not occur"
    }

[<Tests>]
let tests =
    testList "AsyncTests" [
        ``Bindable CancellableTask``
        ``AsyncResult passes along cancellationToken``
        ``Foo``
    ]
