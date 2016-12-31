﻿module PerformanceTests =
  open FSharp.Core.Printf

  open System
  open System.Collections
  open System.Diagnostics
  open System.IO
  open System.Text
  open System.Threading.Tasks

  // now () returns current time in milliseconds since start
  let now : unit -> int64 =
    let sw = Stopwatch ()
    sw.Start ()
    fun () -> sw.ElapsedMilliseconds

  // time estimates the time 'action' repeated a number of times
  let time repeat action =
    let inline cc i       = System.GC.CollectionCount i

    let v                 = action ()

    System.GC.Collect (2, System.GCCollectionMode.Forced, true)

    let bcc0, bcc1, bcc2  = cc 0, cc 1, cc 2
    let b                 = now ()

    for i in 1..repeat do
      action () |> ignore

    let e = now ()
    let ecc0, ecc1, ecc2  = cc 0, cc 1, cc 2

    v, (e - b), ecc0 - bcc0, ecc1 - bcc1, ecc2 - bcc2

  let inline dbreak () = System.Diagnostics.Debugger.Break ()

  type TestResult =  TestResult of string*string*string*int64*int*int*int
  let testResult t y x tm cc0 cc1 cc2 = TestResult (t, y, x, tm, cc0, cc1, cc2)
  let testClass (TestResult (t, _, _, _, _, _, _))  = t
  let testCase  (TestResult (_, tc, _, _, _, _, _)) = tc
  let testX     (TestResult (_, _, x, _, _, _, _))  = x

  type HotData =
    struct
      [<DefaultValue>] val mutable x0 : float
      [<DefaultValue>] val mutable x1 : float
      [<DefaultValue>] val mutable x2 : float
      [<DefaultValue>] val mutable x3 : float
    end

  type ColdData =
    struct
      [<DefaultValue>] val mutable x0 : float
      [<DefaultValue>] val mutable x1 : float
      [<DefaultValue>] val mutable x2 : float
      [<DefaultValue>] val mutable x3 : float
      [<DefaultValue>] val mutable x4 : float
      [<DefaultValue>] val mutable x5 : float
      [<DefaultValue>] val mutable x6 : float
      [<DefaultValue>] val mutable x7 : float
      [<DefaultValue>] val mutable x8 : float
      [<DefaultValue>] val mutable x9 : float
      [<DefaultValue>] val mutable xA : float
      [<DefaultValue>] val mutable xB : float
      [<DefaultValue>] val mutable xC : float
      [<DefaultValue>] val mutable xD : float
      [<DefaultValue>] val mutable xE : float
      [<DefaultValue>] val mutable xF : float
    end

  [<NoComparison>]
  [<NoEquality>]
  type Vector =
    struct
      val x : float
      val y : float
      val z : float

      new (x, y, z) = { x = x; y = y; z = z }

      member x.X = x.x
      member x.Y = x.y
      member x.Z = x.z

      override x.ToString () = sprintf "{%f, %f, %f}" x.x x.y x.z

      static member ( + ) (x : Vector, y : Vector) = Vector (x.x + y.x, x.y + y.y , x.z + y.z )
      static member ( - ) (x : Vector, y : Vector) = Vector (x.x - y.x, x.y - y.y , x.z - y.z )
      static member ( * ) (s : float , x : Vector) = Vector (s * x.x  , s * x.y   , s * x.z   )

      static member New x y z = Vector (x, y, z)

      static member Zero = Vector (0., 0., 0.)
    end

  type InitParticle =
    {
      Mass      : float
      Position  : Vector
      Velocity  : Vector
    }

    static member New m p v : InitParticle = { Mass = m; Position = p; Velocity = v } 

  let globalAcceleration = Vector (0., 1., 0.)

  let fps       = 64.
  let timeStep  = 1. / fps

  module Random =
    let create (seed : int) =
      let mutable state = int64 seed
      let m = 0x7FFFFFFFL // 2^31 - 1
      let d = 1. / float m
      let a = 48271L      // MINSTD
      let c = 0L
      fun () ->
        state <- (a*state + c) % m
        float state * d
    
    let range random b e = 
      let r = random ()
      let v = float (e - b)*r + float b |> int
      v

    let shuffle random vs =
      let a = Array.copy vs
      for i in 0..(vs.Length - 2) do
        let s =  range random i vs.Length
        let t =  a.[s]
        a.[s] <- a.[i]
        a.[i] <- t
      a

    let shuffleWith (shuffle : float []) vs =
      let a = Array.copy vs
      for i in 0..(vs.Length - 2) do
        let f =  shuffle.[i % shuffle.Length]
        let s =  float (vs.Length - i)*f + float i |> int
        let t =  a.[s]
        a.[s] <- a.[i]
        a.[i] <- t
      a

    let mass random           = 100. * random ()
    let vector m random       = Vector.New (random () |> m) (random () |> m) (random () |> m)
    let position random       = vector (( * ) 1000.)  random
    let velocity random       = vector (( + ) -0.5)   random
    let initParticle random   = InitParticle.New (mass random) (position random) (velocity random)

    let array count random creator =
      let ps = Array.zeroCreate count
      for i in 0..(count - 1) do
        ps.[i] <- creator random
      ps

#if DEBUG
  let inline debugSumBy f vs =
    Array.sumBy f vs
#endif

  module ClassPerf =
    [<NoComparison>]
    [<NoEquality>]
    type Particle =
      class
        val mutable mass      : float
        val mutable current   : Vector
        val mutable previous  : Vector
        val mutable hot       : HotData
        val mutable cold      : ColdData

        new (mass, current, previous) = { mass = mass; current = current; previous = previous; hot = HotData (); cold = ColdData () }

        member x.Mass     = x.mass
        member x.Position = x.current   

        member x.Verlet globalAcceleration =
          let next    =  x.current + x.current - x.previous + globalAcceleration
          x.previous  <- x.current
          x.current   <- next

        static member New timeStep mass position velocity = 
          let current   = position
          let previous  = position - (timeStep * (velocity : Vector))
          Particle (mass, current, previous)

      end

    let createTestCases initParticles shuffle =
      let map               = Array.map (fun (ip : InitParticle) -> Particle.New timeStep ip.Mass ip.Position ip.Velocity)
      let particles         = map initParticles
      let shuffledParticles = Random.shuffleWith shuffle particles

      let rec verletLoop (particles : Particle []) globalAcceleration i =
        if i < particles.Length then
          particles.[i].Verlet globalAcceleration
          verletLoop particles globalAcceleration (i + 1)

#if DEBUG
      let verlet ()         = 
        let previous  = particles |> debugSumBy (fun p -> p.Position) 
        verletLoop particles globalAcceleration 0
        let current   = particles |> debugSumBy (fun p -> p.Position) 
        previous, current
#else
      let verlet ()         = verletLoop particles          globalAcceleration 0
#endif
      let shuffledVerlet () = verletLoop shuffledParticles  globalAcceleration 0

      verlet, shuffledVerlet

  module StructPerf =
    [<NoComparison>]
    [<NoEquality>]
    type Particle =
      struct
        val mutable mass      : float
        val mutable current   : Vector
        val mutable previous  : Vector
        val mutable hot       : HotData
        val mutable cold      : ColdData

        new (mass, current, previous) = { mass = mass; current = current; previous = previous; hot = HotData(); cold = ColdData () }

        member x.Mass     = x.mass
        member x.Position = x.current   

        member x.Verlet globalAcceleration =
          let next    =  x.current + x.current - x.previous + globalAcceleration
          x.previous  <- x.current
          x.current   <- next

        static member New timeStep mass position velocity = 
          let current   = position
          let previous  = position - (timeStep * (velocity : Vector))
          Particle (mass, current, previous)

      end

    let createTestCases initParticles shuffle =
      let map               = Array.map (fun (ip : InitParticle) -> Particle.New timeStep ip.Mass ip.Position ip.Velocity)
      let particles         = map initParticles
      let shuffledParticles = Random.shuffleWith shuffle particles

      let rec verletLoop (particles : Particle []) globalAcceleration i =
        if i < particles.Length then
          particles.[i].Verlet globalAcceleration
          verletLoop particles globalAcceleration (i + 1)

#if DEBUG
      let verlet ()         = 
        let previous  = particles |> debugSumBy (fun p -> p.Position) 
        verletLoop particles globalAcceleration 0
        let current   = particles |> debugSumBy (fun p -> p.Position) 
        previous, current
#else
      let verlet ()         = verletLoop particles          globalAcceleration 0
#endif
      let shuffledVerlet () = verletLoop shuffledParticles  globalAcceleration 0

      verlet, shuffledVerlet

  module HotAndColdPerf =
    [<NoComparison>]
    [<NoEquality>]
    type Particle =
      struct
        val mutable mass      : float
        val mutable current   : Vector
        val mutable previous  : Vector
        val mutable hot       : HotData

        new (mass, current, previous) = { mass = mass; current = current; previous = previous; hot = HotData(); }

        member x.Mass     = x.mass
        member x.Position = x.current   

        member x.Verlet globalAcceleration =
          let next    =  x.current + x.current - x.previous + globalAcceleration
          x.previous  <- x.current
          x.current   <- next

        static member New timeStep mass position velocity = 
          let current   = position
          let previous  = position - (timeStep * (velocity : Vector))
          Particle (mass, current, previous)

      end

    let createTestCases initParticles shuffle =
      let map               = Array.map (fun (ip : InitParticle) -> Particle.New timeStep ip.Mass ip.Position ip.Velocity)
      let particles         = map initParticles
      let shuffledParticles = Random.shuffleWith shuffle particles
      let coldDatum         = Array.create initParticles.Length (ColdData ())

      let rec verletLoop (particles : Particle []) coldDatum globalAcceleration i =
        if i < particles.Length then
          particles.[i].Verlet globalAcceleration
          verletLoop particles coldDatum globalAcceleration (i + 1)

#if DEBUG
      let verlet ()         = 
        let previous  = particles |> debugSumBy (fun p -> p.Position) 
        verletLoop particles coldDatum globalAcceleration 0
        let current   = particles |> debugSumBy (fun p -> p.Position)
        previous, current
#else
      let verlet ()         = verletLoop particles         coldDatum globalAcceleration 0
#endif
      let shuffledVerlet () = verletLoop shuffledParticles coldDatum globalAcceleration 0

      verlet, shuffledVerlet

  module StructuresOfArraysPerf =
    type Selection =
      | A
      | B
    [<NoComparison>]
    [<NoEquality>]
    type Particles (timeStep, initParticles : InitParticle []) =
      class
        let count                    = initParticles.Length
        let masses      : float   [] = initParticles |> Array.map (fun ip -> ip.Mass)
        let positionsA  : Vector  [] = initParticles |> Array.map (fun ip -> ip.Position)
        let positionsB  : Vector  [] = initParticles |> Array.mapi (fun i ip -> 
          let position = positionsA.[i]
          let velocity = ip.Velocity
          position - (timeStep * velocity)
          )
        let hotData     : HotData [] = Array.zeroCreate count
        let coldData    : ColdData[] = Array.zeroCreate count

        let mutable selection = Selection.A

        let rec loop globalAcceleration (a : Vector []) (b : Vector []) i = 
          if i < count then
            let current   =   a.[i]
            let previous  =   b.[i]
            let next      =   current + current - previous + globalAcceleration
            b.[i]         <-  next
            loop globalAcceleration a b (i + 1)

        member x.Verlet globalAcceleration =
          selection <- 
            match selection with
            | Selection.A ->
              loop globalAcceleration positionsA positionsB 0
              Selection.B
            | Selection.B ->
              loop globalAcceleration positionsB positionsA 0
              Selection.A

        member x.Positions =
          match selection with
          | Selection.A -> positionsA
          | Selection.B -> positionsB

      end

    let createTestCases initParticles shuffle =
      let particles         = Particles (timeStep, initParticles)
      let shuffledParticles = particles // TODO: Shuffle

#if DEBUG
      let verlet ()         = 
        let previous = particles.Positions |> debugSumBy id
        particles.Verlet globalAcceleration
        let current  = particles.Positions |> debugSumBy id
        previous, current
#else
      let verlet ()         = particles.Verlet          globalAcceleration
#endif
      let shuffledVerlet () = shuffledParticles.Verlet  globalAcceleration

      verlet, shuffledVerlet

  let run () =
#if DEBUG
    let count   = 1000000
#else
    let count   = 10000000
#endif
    let inners  =
#if DEBUG
      [|
        10
        1000
        100000
      |]
#else
(*
      let a c = Array.init 10 (fun i -> c + c*i)
      Array.concat
        [|
          a 10
          a 100
          a 1000
          a 10000
          a 100000
        |]
*)
      let samples = 50
      let minimum = 100
      let maximum = 1000000
      let exp     = Math.Pow (float (maximum / minimum), 1. / float (samples - 1))
      Array.init samples (fun i -> ((float minimum * pown exp i) |> round |> int))
#endif

    let testCases =
      [|
        "Class"                   , ClassPerf.createTestCases
        "Struct"                  , StructPerf.createTestCases
        "Hot & Cold"              , HotAndColdPerf.createTestCases
        "Structures of Arrays"    , StructuresOfArraysPerf.createTestCases
      |]

    let random = Random.create 19740531

    let results = ResizeArray 16

    for inner in inners do

      let outer = count / inner

      printfn "Running test cases with outer=%d, inner=%d" outer inner

      printfn "    Creating particles"
      let initParticles   = Random.array inner random Random.initParticle
      let shuffle         = Array.init inner (fun _ -> random ())

      for name, creator in testCases do
        printfn "  Test case %A" name

        let result t a =
          printfn "    Runnning test case: %s" t
          let v, time, cc0, cc1, cc2 = time outer a
          results.Add <| testResult t name (sprintf "%d" inner) time cc0 cc1 cc2
          printfn "      = %A" v

        printfn "    Creating test cases"
        let verlet, shuffledVerlet = creator initParticles shuffle

        result "Verlet"           verlet
        result "Verlet(Shuffled)" shuffledVerlet

    let results = results.ToArray ()

    let testXs      = results |> Array.groupBy testX |> Array.map fst
    let testCases   = results |> Array.groupBy testCase
    let testClasses = results |> Array.groupBy testClass

    let header  = "Name" + (testXs |> Array.map (fun i -> ",'" + string i) |> Array.reduce (+))

    for testClassName, _ in testClasses do
      use perf      = new StreamWriter ("perf_" + testClassName + ".csv")
      use cc        = new StreamWriter ("cc_"   + testClassName + ".csv")
      let line sw l = (sw : StreamWriter).WriteLine (l : string)
      let linef sw f= kprintf (line sw) f

      line perf header
      line cc   header

      for testCaseName, testCaseResults in testCases do
        let write sb s  = (sb : StringBuilder).Append (s : string) |> ignore
        let field sb s  = (sb : StringBuilder).Append ',' |> ignore; write sb s
        let fieldf sb f = kprintf (field sb) f
        let psb         = StringBuilder 16
        let csb         = StringBuilder 16
        write psb testCaseName
        write csb testCaseName
        let m = testCaseResults |> Array.map (fun tr -> (testClass tr, testX tr), tr) |> Map.ofArray
        for testX in testXs do
          let (TestResult (_, _, _, tm, cc0 ,_, _)) = m.[testClassName, testX]
          fieldf psb "%d" tm
          fieldf csb "%d" cc0

        line perf <| psb.ToString ()
        line cc   <| csb.ToString ()

open System

[<EntryPoint>]
let main argv =
  try
    Environment.CurrentDirectory <- AppDomain.CurrentDomain.BaseDirectory
    PerformanceTests.run ()
    0
  with
  | e ->
    printfn "Caught: %s" e.Message
    999
