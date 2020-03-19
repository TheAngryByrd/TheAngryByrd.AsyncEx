// TaskBuilder.fs - TPL task computation expressions for F#
//
// Written in 2016 by Robert Peele (humbobst@gmail.com)
// New operator-based overload resolution for F# 4.0 compatibility by Gustavo Leon in 2018.
//
// To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights
// to this software to the public domain worldwide. This software is distributed without any warranty.
//
// You should have received a copy of the CC0 Public Domain Dedication along with this software.
// If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.

namespace TAB.FSharp.Control.Tasks
open System
open System.Threading.Tasks
open System.Runtime.CompilerServices
open System.Threading

// This module is not really obsolete, but it's not intended to be referenced directly from user code.
// However, it can't be private because it is used within inline functions that *are* user-visible.
// Marking it as obsolete is a workaround to hide it from auto-completion tools.
[<Obsolete>]
module TaskBuilder =
    /// Represents the state of a computation:
    /// either awaiting something with a continuation,
    /// or completed with a return value.
    type Step<'a> =
        | Await of ICriticalNotifyCompletion * (CancellationToken -> Step<'a>) * CancellationToken
        | Return of 'a * CancellationToken
        /// We model tail calls explicitly, but still can't run them without O(n) memory usage.
        | ReturnFrom of 'a Task * CancellationToken
    /// Implements the machinery of running a `Step<'m, 'm>` as a task returning a continuation task.
    and StepStateMachine<'a>(parentCt, firstStep) as this =
        let methodBuilder = AsyncTaskMethodBuilder<'a Task>()
        /// The continuation we left off awaiting on our last MoveNext().
        let mutable continuation = fun () -> firstStep
        /// Returns next pending awaitable or null if exiting (including tail call).
        let nextAwaitable() =
            try
                match continuation() with
                | Return (r, ct) ->
                    let cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                    printfn "Return"
                    cts.Token.ThrowIfCancellationRequested()
                    printfn "Return2"
                    methodBuilder.SetResult(Task.FromResult(r))
                    null
                | ReturnFrom (t, ct) ->
                    let cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                    printfn "ReturnFrom"
                    cts.Token.ThrowIfCancellationRequested()
                    printfn "ReturnFrom2"
                    methodBuilder.SetResult(t)
                    null
                | Await (await, next, ct) ->
                    printfn "Await"
                    let cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                    cts.Token.ThrowIfCancellationRequested()
                    // ct.ThrowIfCancellationRequested()
                    printfn "Await2"
                    continuation <-
                        fun () ->
                            use cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                            cts.Token.ThrowIfCancellationRequested()
                            next cts.Token
                    await
            with
            | :? OperationCanceledException as e ->
                // printfn "OperationCanceledException -> %A" e
                methodBuilder.SetResult(Task.FromCanceled<_> e.CancellationToken)
                null
            | exn ->
                printfn "--> %A" exn
                methodBuilder.SetException(exn)
                null
        let mutable self = this

        /// Start execution as a `Task<Task<'a>>`.
        member __.Run() =
            methodBuilder.Start(&self)
            methodBuilder.Task

        interface IAsyncStateMachine with
            /// Proceed to one of three states: result, failure, or awaiting.
            /// If awaiting, MoveNext() will be called again when the awaitable completes.
            member __.MoveNext() =
                let mutable await = nextAwaitable()
                if not (isNull await) then
                    // Tell the builder to call us again when this thing is done.
                    methodBuilder.AwaitUnsafeOnCompleted(&await, &self)
            member __.SetStateMachine(_) = () // Doesn't really apply since we're a reference type.

    let unwrapException (agg : AggregateException) =
        let inners = agg.InnerExceptions
        if inners.Count = 1 then inners.[0]
        else agg :> Exception

    /// Used to represent no-ops like the implicit empty "else" branch of an "if" expression.
    let zero = Return ((), CancellationToken.None)

    /// Used to return a value.
    let ret ct (x : 'a) = Return (x, ct)

    type Binder<'out> =
        // We put the output generic parameter up here at the class level, so it doesn't get subject to
        // inline rules. If we put it all in the inline function, then the compiler gets confused at the
        // below and demands that the whole function either is limited to working with (x : obj), or must
        // be inline itself.
        //
        // let yieldThenReturn (x : 'a) =
        //     task {
        //         do! Task.Yield()
        //         return x
        //     }

        static member inline GenericAwait< ^abl, ^awt, ^inp
                                            when ^abl : (member GetAwaiter : unit -> ^awt)
                                            and ^awt :> ICriticalNotifyCompletion
                                            and ^awt : (member get_IsCompleted : unit -> bool)
                                            and ^awt : (member GetResult : unit -> ^inp) >
            (abl : CancellationToken -> ^abl, continuation : ^inp -> 'out Step, ct) : 'out Step =
                let awt ct= (^abl : (member GetAwaiter : unit -> ^awt)(abl ct)) // get an awaiter from the awaitable
                if (^awt : (member get_IsCompleted : unit -> bool)(awt ct)) then // shortcut to continue immediately
                    continuation (^awt : (member GetResult : unit -> ^inp)(awt ct))
                else
                    Await (awt ct, (fun (ct) -> continuation (^awt : (member GetResult : unit -> ^inp)(awt ct))), ct)

        static member inline GenericAwaitConfigureFalse< ^tsk, ^abl, ^awt, ^inp
                                                        when ^tsk : (member ConfigureAwait : bool -> ^abl)
                                                        and ^abl : (member GetAwaiter : unit -> ^awt)
                                                        and ^awt :> ICriticalNotifyCompletion
                                                        and ^awt : (member get_IsCompleted : unit -> bool)
                                                        and ^awt : (member GetResult : unit -> ^inp) >
            (tsk : CancellationToken -> ^tsk, continuation : ^inp -> 'out Step, ct) : 'out Step =
                let abl ct = (^tsk : (member ConfigureAwait : bool -> ^abl)(tsk ct, false))
                Binder<'out>.GenericAwait(abl, continuation, ct)

    /// Special case of the above for `Task<'a>`. Have to write this out by hand to avoid confusing the compiler
    /// trying to decide between satisfying the constraints with `Task` or `Task<'a>`.
    let bindTask ct (task : 'a Task) (continuation : 'a -> Step<'b>) =
        let awt = task.GetAwaiter()
        if awt.IsCompleted then // Proceed to the next step based on the result we already have.
            continuation(awt.GetResult())
        else // Await and continue later when a result is available.
            Await (awt, (fun (ct) -> continuation(awt.GetResult())), ct)

    /// Special case of the above for `Task<'a>`, for the context-insensitive builder.
    /// Have to write this out by hand to avoid confusing the compiler thinking our built-in bind method
    /// defined on the builder has fancy generic constraints on inp and out parameters.
    let bindTaskConfigureFalse ct (task : 'a Task) (continuation : 'a -> Step<'b>) =
        let awt = task.ConfigureAwait(false).GetAwaiter()
        if awt.IsCompleted then // Proceed to the next step based on the result we already have.
            continuation(awt.GetResult())
        else // Await and continue later when a result is available.
            Await (awt, (fun (ct) -> continuation(awt.GetResult())), ct)

    /// Chains together a step with its following step.
    /// Note that this requires that the first step has no result.
    /// This prevents constructs like `task { return 1; return 2; }`.
    let rec combine (step : Step<unit>) (continuation : CancellationToken -> Step<'b>) =
        match step with
        | Return (_, ct) -> continuation ct
        | ReturnFrom (t, ct) ->
            Await (t.GetAwaiter(), continuation, ct)
        | Await (awaitable, next, ct) ->
            Await (awaitable, (fun (ct) -> combine (next ct) continuation), ct)

    /// Builds a step that executes the body while the condition predicate is true.
    let whileLoop ct (cond : unit -> bool) (body : CancellationToken -> Step<unit>) =
        if cond() then
            // Create a self-referencing closure to test whether to repeat the loop on future iterations.
            let rec repeat (ct) =
                if cond() then
                    let body = body ct
                    match body with
                    | Return _ -> repeat ct
                    | ReturnFrom (t, ct) -> Await(t.GetAwaiter(), repeat, ct)
                    | Await (awaitable, next, ct) ->
                        Await (awaitable, (fun (ct) -> combine (next ct) repeat), ct)
                else zero
            // Run the body the first time and chain it to the repeat logic.
            combine (body ct) repeat
        else zero

    /// Wraps a step in a try/with. This catches exceptions both in the evaluation of the function
    /// to retrieve the step, and in the continuation of the step (if any).
    let rec tryWith ct (step : CancellationToken -> Step<'a>) (catch : exn -> Step<'a>) =
        try
            match step ct with
            | Return _ as i -> i
            | ReturnFrom (t, ct) ->
                let awaitable = t.GetAwaiter()
                let next = fun (ct) ->
                    try
                        Return(awaitable.GetResult(), ct)
                    with
                    | exn -> catch exn
                Await(awaitable, next, ct)
            | Await (awaitable, next, ct) -> Await (awaitable, (fun (ct) -> tryWith ct next catch), ct)
        with
        | exn -> catch exn

    /// Wraps a step in a try/finally. This catches exceptions both in the evaluation of the function
    /// to retrieve the step, and in the continuation of the step (if any).
    let rec tryFinally ct (step : CancellationToken -> Step<'a>) fin =
        let step =
            try step ct
            // Important point: we use a try/with, not a try/finally, to implement tryFinally.
            // The reason for this is that if we're just building a continuation, we definitely *shouldn't*
            // execute the `fin()` part yet -- the actual execution of the asynchronous code hasn't completed!
            with
            | _ ->
                fin()
                reraise()
        match step with
        | Return _ as i ->
            fin()
            i
        | ReturnFrom (t, ct) ->
            let awaitable = t.GetAwaiter()
            let next = fun (ct) ->
                let result =
                    try
                        Return(awaitable.GetResult(), ct)
                    with
                    | _ ->
                        fin()
                        reraise()
                fin() // if we got here we haven't run fin(), because we would've reraised after doing so
                result
            Await(awaitable,next, ct )
        | Await (awaitable, next, ct) ->
            Await (awaitable, (fun (ct) -> tryFinally ct next fin), ct)

    /// Implements a using statement that disposes `disp` after `body` has completed.
    let using ct (disp : #IDisposable) (body : _ -> Step<'a>) =
        // A using statement is just a try/finally with the finally block disposing if non-null.
        tryFinally ct
            (fun (ct) -> body disp) //TODO
            (fun () -> if not (isNull (box disp)) then disp.Dispose())

    /// Implements a loop that runs `body` for each element in `sequence`.
    let forLoop ct (sequence : 'a seq) (body : 'a -> Step<unit>) =
        // A for loop is just a using statement on the sequence's enumerator...
        using ct (sequence.GetEnumerator())
            // ... and its body is a while loop that advances the enumerator and runs the body on each element.
            (fun e -> whileLoop ct e.MoveNext (fun (ct) -> body e.Current)) //TODO

    /// Runs a step as a task -- with a short-circuit for immediately completed steps.
    let run parentCt (firstStep : unit -> Step<'a>) =
        try
            match firstStep() with
            | Return (x, ct) ->
                use cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                printfn "run.Return"
                cts.Token.ThrowIfCancellationRequested ()
                Task.FromResult(x)
            | ReturnFrom (t, ct) ->
                use cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                printfn "run.ReturnFrom"
                cts.Token.ThrowIfCancellationRequested ()
                t
            | Await (_,_,ct) as step ->
                printfn "run.Await"
                use cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, ct)
                if cts.Token.IsCancellationRequested
                then
                    let cts = new TaskCompletionSource<_>(cts.Token)
                    cts.SetCanceled ()
                    cts.Task
                else
                    StepStateMachine<'a>(cts.Token, step).Run().Unwrap() // sadly can't do tail recursion
        // Any exceptions should go on the task, rather than being thrown from this call.
        // This matches C# behavior where you won't see an exception until awaiting the task,
        // even if it failed before reaching the first "await".
        with
        | exn ->
            let src = new TaskCompletionSource<_>()
            src.SetException(exn)
            src.Task

    // We have to have a dedicated overload for Task<'a> so the compiler doesn't get confused with Convenience overloads for Asyncs
    // Everything else can use bindGenericAwaitable via an extension member

    type Priority3 = obj
    type Priority2 = IComparable

    // type BindS = Priority1 with
    //     static member inline (>>=) (_:Priority2, taskLike : 't) = fun (k:  _ -> 'b Step) -> Binder<'b>.GenericAwait (taskLike, k): 'b Step
    //     static member        (>>=) (  Priority1, task: 'a Task) = fun (k: 'a -> 'b Step) -> bindTask task k                      : 'b Step
    //     static member        (>>=) (  Priority1, a  : 'a Async) = fun (k: 'a -> 'b Step) -> bindTask (Async.StartAsTask a) k     : 'b Step

    // type ReturnFromS = Priority1 with
    //     static member inline ($) (Priority1, taskLike    ) = Binder<_>.GenericAwait (taskLike, ret)
    //     static member        ($) (Priority1, a : 'a Async) = bindTask (Async.StartAsTask a) ret : Step<'a>


    let toTaskUnit (t : Task) =
        let tcs = TaskCompletionSource()
        t.ContinueWith(fun t ->
            if t.IsCanceled then
                printfn "IsCanceled"
                tcs.SetCanceled()
            else if t.IsFaulted then
                printfn "IsFaulted"
                tcs.SetException t.Exception
            else
                printfn "result"
                tcs.SetResult()
            ) |> ignore
        tcs.Task
    type BindI = Priority1 with
        static member inline (>>=) (_:Priority3, taskLike            : CancellationToken -> 't) = fun (ct : CancellationToken) (k :  _ -> 'b Step) -> Binder<'b>.GenericAwait (taskLike, k, ct)                          : 'b Step
        static member inline (>>=) (_:Priority3, taskLike            : 't) = fun (ct : CancellationToken) (k :  _ -> 'b Step) -> Binder<'b>.GenericAwait ((fun ct -> taskLike), k, ct)                          : 'b Step
        static member inline (>>=) (_:Priority2, configurableTaskLike: CancellationToken -> 't) = fun (ct : CancellationToken) (k :  _ -> 'b Step) -> Binder<'b>.GenericAwaitConfigureFalse (configurableTaskLike, k, ct): 'b Step
        static member inline (>>=) (_:Priority2, configurableTaskLike: 't) = fun (ct : CancellationToken) (k :  _ -> 'b Step) -> Binder<'b>.GenericAwaitConfigureFalse ((fun ct -> configurableTaskLike), k, ct): 'b Step
        static member        (>>=) (  Priority1, task: CancellationToken -> Task) = fun (ct : CancellationToken) (k : _ -> 'b Step) -> bindTaskConfigureFalse ct (task ct |> toTaskUnit) k                                  : 'b Step
        static member        (>>=) (  Priority1, task: CancellationToken -> 'a Task) = fun (ct : CancellationToken) (k : 'a -> 'b Step) -> bindTaskConfigureFalse ct (task ct) k                                  : 'b Step
        static member        (>>=) (  Priority1, task: Task           ) = fun (ct : CancellationToken) (k : _ -> 'b Step) -> bindTaskConfigureFalse ct (task |> toTaskUnit) k                                  : 'b Step
        static member        (>>=) (  Priority1, task: 'a Task           ) = fun (ct : CancellationToken) (k : 'a -> 'b Step) -> bindTaskConfigureFalse ct task k                                  : 'b Step
        static member        (>>=) (  Priority1, a   : 'a Async          ) = fun (ct : CancellationToken) (k : 'a -> 'b Step) -> bindTaskConfigureFalse ct (Async.StartAsTask(a, cancellationToken = ct)) k                 : 'b Step

    type ReturnFromI = Priority1 with
        static member inline ($) (_:Priority2, taskLike            ) = Binder<_>.GenericAwait(taskLike, ret CancellationToken.None, CancellationToken.None)
        // static member inline ($) (  Priority1, configurableTaskLike) = Binder<_>.GenericAwaitConfigureFalse(configurableTaskLike, ret)
        static member        ($) (  Priority1, a   : 'a Async      ) = bindTaskConfigureFalse CancellationToken.None (Async.StartAsTask a) (ret CancellationToken.None)
        static member        ($) (  Priority1, task: 'a Task       ) = bindTaskConfigureFalse CancellationToken.None task (ret CancellationToken.None)

    // New style task builder.
    type TaskBuilderV2(?ct : CancellationToken) =
        let disposables = ResizeArray<IDisposable>()
        let mutable innerCt = Option.defaultValue CancellationToken.None ct
        let mutable disposed = false
        let cleanup () =
            try
                printfn "cleanup %i" disposables.Count
                disposables |> Seq.iter(fun d -> d.Dispose())
            with
                e -> ()

        member __.CancellationToken = innerCt
        // These methods are consistent between all builders.
        member __.Delay(f : CancellationToken -> Step<_>) = f
        member __.Run(f : CancellationToken -> Step<'m>) =
            fun ct ->
                let cts = CancellationTokenSource.CreateLinkedTokenSource(ct, __.CancellationToken)
                disposables.Add cts
                innerCt <- cts.Token
                (run cts.Token (fun () -> f cts.Token)).ContinueWith(fun (t : Task<_>) ->
                    cleanup()
                    t).Unwrap()
        member __.Zero() = zero
        member __.Return(x) = ret __.CancellationToken x
        member __.Combine(step : unit Step, continuation) = combine step continuation
        member __.While(condition : unit -> bool, body : CancellationToken -> unit Step) = whileLoop __.CancellationToken condition body
        member __.For(sequence : _ seq, body : _ -> unit Step) = forLoop __.CancellationToken sequence body
        member __.TryWith(body : CancellationToken -> _ Step, catch : exn -> _ Step) = tryWith __.CancellationToken body catch
        member __.TryFinally(body : CancellationToken -> _ Step, fin : unit -> unit) = tryFinally __.CancellationToken body fin
        member __.Using(disp : #IDisposable, body : #IDisposable -> _ Step) = using __.CancellationToken disp body
        member __.ReturnFrom a : _ Step = ReturnFrom a
        // interface IDisposable with
        //     member self.Dispose() =
        //         cleanup()
        //         GC.SuppressFinalize(self)
        // override __.Finalize() =
        //     cleanup()
    // Old style task builder. Retained for binary compatibility.
    // type TaskBuilder() =
    //     // These methods are consistent between the two builders.
    //     // Unfortunately, inline members do not work with inheritance.
    //     member inline __.Delay(f : unit -> Step<_>) = f
    //     member inline __.Run(f : unit -> Step<'m>) = run f
    //     member inline __.Zero() = zero
    //     member inline __.Return(x) = ret x
    //     member inline __.Combine(step : unit Step, continuation) = combine step continuation
    //     member inline __.While(condition : unit -> bool, body : unit -> unit Step) = whileLoop condition body
    //     member inline __.For(sequence : _ seq, body : _ -> unit Step) = forLoop sequence body
    //     member inline __.TryWith(body : unit -> _ Step, catch : exn -> _ Step) = tryWith body catch
    //     member inline __.TryFinally(body : unit -> _ Step, fin : unit -> unit) = tryFinally body fin
    //     member inline __.Using(disp : #IDisposable, body : #IDisposable -> _ Step) = using disp body
    //     // End of consistent methods -- the following methods are different between
    //     // `TaskBuilder` and `ContextInsensitiveTaskBuilder`!

    //     // We have to have a dedicated overload for Task<'a> so the compiler doesn't get confused.
    //     // Everything else can use bindGenericAwaitable via an extension member (defined later).
    //     member inline __.ReturnFrom(task : _ Task) = ReturnFrom task
    //     member inline __.Bind(task : 'a Task, continuation : 'a -> 'b Step) : 'b Step =
    //         bindTask task continuation

    // // Old style task builder. Retained for binary compatibility.
    // type ContextInsensitiveTaskBuilder() =
    //     // These methods are consistent between the two builders.
    //     // Unfortunately, inline members do not work with inheritance.
    //     member inline __.Delay(f : unit -> Step<_>) = f
    //     member inline __.Run(f : unit -> Step<'m>) = run f
    //     member inline __.Zero() = zero
    //     member inline __.Return(x) = ret x
    //     member inline __.Combine(step : unit Step, continuation) = combine step continuation
    //     member inline __.While(condition : unit -> bool, body : unit -> unit Step) = whileLoop condition body
    //     member inline __.For(sequence : _ seq, body : _ -> unit Step) = forLoop sequence body
    //     member inline __.TryWith(body : unit -> _ Step, catch : exn -> _ Step) = tryWith body catch
    //     member inline __.TryFinally(body : unit -> _ Step, fin : unit -> unit) = tryFinally body fin
    //     member inline __.Using(disp : #IDisposable, body : #IDisposable -> _ Step) = using disp body
    //     // End of consistent methods -- the following methods are different between
    //     // `TaskBuilder` and `ContextInsensitiveTaskBuilder`!

    //     // We have to have a dedicated overload for Task<'a> so the compiler doesn't get confused.
    //     // Everything else can use bindGenericAwaitable via an extension member (defined later).
    //     member inline __.ReturnFrom(task : _ Task) = ReturnFrom task
    //     member inline __.Bind(task : 'a Task, continuation : 'a -> 'b Step) : 'b Step =
    //         bindTaskConfigureFalse task continuation


// Don't warn about our use of the "obsolete" module we just defined (see notes at start of file).
#nowarn "44"

// [<AutoOpen>]
// module ContextSensitive =
//     /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method.
//     /// Use this like `task { let! taskResult = someTask(); return taskResult.ToString(); }`.
//     let task = TaskBuilder.TaskBuilder()

//     [<Obsolete("It is no longer necessary to wrap untyped System.Thread.Tasks.Task objects with \"unitTask\".")>]
//     let inline unitTask t = t :> Task

//     // These are fallbacks when the Bind and ReturnFrom on the builder object itself don't apply.
//     // This is how we support binding arbitrary task-like types.
//     type TaskBuilder.TaskBuilder with
//         member inline this.ReturnFrom(taskLike) =
//             TaskBuilder.Binder<_>.GenericAwait(taskLike, TaskBuilder.ret)
//         member inline this.Bind(taskLike, continuation : _ -> 'a TaskBuilder.Step) : 'a TaskBuilder.Step =
//             TaskBuilder.Binder<'a>.GenericAwait(taskLike, continuation)
//         // Convenience overloads for Asyncs.
//         member __.ReturnFrom(a : 'a Async) =
//             TaskBuilder.bindTask (Async.StartAsTask a) TaskBuilder.ret
//         member __.Bind(a : 'a Async, continuation : 'a -> 'b TaskBuilder.Step) : 'b TaskBuilder.Step =
//             TaskBuilder.bindTask (Async.StartAsTask a) continuation

// module ContextInsensitive =
//     /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method, but with
//     /// all awaited tasks automatically configured *not* to resume on the captured context.
//     /// This is often preferable when writing library code that is not context-aware, but undesirable when writing
//     /// e.g. code that must interact with user interface controls on the same thread as its caller.
//     let task = TaskBuilder.ContextInsensitiveTaskBuilder()

//     [<Obsolete("It is no longer necessary to wrap untyped System.Thread.Tasks.Task objects with \"unitTask\".")>]
//     let inline unitTask (t : Task) = t.ConfigureAwait(false)

//     // These are fallbacks when the Bind and ReturnFrom on the builder object itself don't apply.
//     // This is how we support binding arbitrary task-like types.
//     type TaskBuilder.ContextInsensitiveTaskBuilder with
//         member inline this.ReturnFrom(taskLike) =
//             TaskBuilder.Binder<_>.GenericAwait(taskLike, TaskBuilder.ret)
//         member inline this.Bind(taskLike, continuation : _ -> 'a TaskBuilder.Step) : 'a TaskBuilder.Step =
//             TaskBuilder.Binder<'a>.GenericAwait(taskLike, continuation)

//         // Convenience overloads for Asyncs.
//         member __.ReturnFrom(a : 'a Async) =
//             TaskBuilder.bindTaskConfigureFalse (Async.StartAsTask a) TaskBuilder.ret
//         member __.Bind(a : 'a Async, continuation : 'a -> 'b TaskBuilder.Step) : 'b TaskBuilder.Step =
//             TaskBuilder.bindTaskConfigureFalse (Async.StartAsTask a) continuation

//     [<AutoOpen>]
//     module HigherPriorityBinds =
//         // When it's possible for these to work, the compiler should prefer them since they shadow the ones above.
//         type TaskBuilder.ContextInsensitiveTaskBuilder with
//             member inline this.ReturnFrom(configurableTaskLike) =
//                 TaskBuilder.Binder<_>.GenericAwaitConfigureFalse(configurableTaskLike, TaskBuilder.ret)
//             member inline this.Bind(configurableTaskLike, continuation : _ -> 'a TaskBuilder.Step) : 'a TaskBuilder.Step =
//                 TaskBuilder.Binder<'a>.GenericAwaitConfigureFalse(configurableTaskLike, continuation)


module V2 =
    // [<AutoOpen>]
    // module ContextSensitive =
    //     open TaskBuilder

    //     /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method.
    //     /// Use this like `task { let! taskResult = someTask(); return taskResult.ToString(); }`.
    //     let task = TaskBuilderV2()

    //     [<Obsolete("It is no longer necessary to wrap untyped System.Thread.Tasks.Task objects with \"unitTask\".")>]
    //     let unitTask (t : Task) = t

    //     type TaskBuilderV2 with
    //         member inline __.Bind (task, continuation : 'a -> 'b Step) : 'b Step = (BindS.Priority1 >>= task) continuation
    //         member inline __.ReturnFrom a                              : 'b Step = ReturnFromS.Priority1 $ a

    module ContextInsensitive =
        open TaskBuilder

        /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method, but with
        /// all awaited tasks automatically configured *not* to resume on the captured context.
        /// This is often preferable when writing library code that is not context-aware, but undesirable when writing
        /// e.g. code that must interact with user interface controls on the same thread as its caller.
        let task = TaskBuilderV2()
        let taskCt ct = TaskBuilderV2(ct)
        [<Obsolete("It is no longer necessary to wrap untyped System.Thread.Tasks.Task objects with \"unitTask\".")>]
        let unitTask (t : Task) = t.ConfigureAwait(false)

        type TaskBuilderV2 with
            member inline __.Bind (task, continuation : 'a -> 'b Step) : 'b Step = (BindI.Priority1 >>= task) __.CancellationToken continuation
            member inline __.ReturnFrom a                              : 'b Step = ReturnFromI.Priority1 $ a
