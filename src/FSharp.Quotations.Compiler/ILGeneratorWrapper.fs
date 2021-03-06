﻿(*
 * FSharp.Quotations.Compiler - a compiler for F# expression tree
 * Written in 2015 by bleis-tift (hey_c_est_la_vie@hotmail.co.jp)
 * kyonmm, zakky-dev
 * 
 * To the extent possible under law, the author(s) have dedicated all copyright
 * and related and neighboring rights to this software to the public domain worldwide.
 * This software is distributed without any warranty.
 * 
 * You should have received a copy of the CC0 Public Domain Dedication along with this software.
 * If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.
 *)
namespace FSharp.Quotations.Compiler

open System
open System.Reflection.Emit
open System.Diagnostics.SymbolStore
open System.IO

type internal ILGeneratorWrapper private (builder: IGeneratorProvider, signature: string, gen: ILGenerator, name: string, doc: ISymbolDocumentWriter option) =
  let mutable lineNumber = 2
  let mutable indentCount = 0
  let mutable localVariableNumber = 0
  let writer = doc |> Option.map (fun _ -> let w = File.CreateText(name + ".il") in w.WriteLine("// " + signature); w)

  let pushIndent () = indentCount <- indentCount + 2
  let popIndent () = indentCount <- indentCount - 2
  let withIndent(str) =
    (String.replicate indentCount " ") + str

  let emittedOpCodes = ResizeArray<_>()

  static member Create(builder, signature, gen, name, doc) =
    ILGeneratorWrapper(builder, signature, gen, name, doc)

  member __.WriteLine(line: string) =
    writer |> Option.iter (fun w ->
      w.WriteLine(withIndent line)
      lineNumber <- lineNumber + 1
    )
  member this.WriteLines(lines: string list) = lines |> List.iter (this.WriteLine)
  member __.WriteLineAndMark(line: string) =
    writer |> Option.iter (fun w ->
      let label = DebugUtil.label gen
      w.WriteLine(withIndent (label + line))
      gen.MarkSequencePoint(doc.Value, lineNumber, indentCount + 1, lineNumber, indentCount + line.Length + 1)
      lineNumber <- lineNumber + 1
    )
  member __.Close() =
    writer |> Option.iter (fun w -> w.Close())
    #if DEVELOPMENT
    if emittedOpCodes.Count >= 3 then
      match emittedOpCodes |> Seq.toList |> List.rev with
      | last::preLast::prePreLast::_ ->
          assert (last = Ret)
          match preLast with
          | Call _ | Callvirt _ -> ()
              // 本当なら有効化しておきたいが、ここで取得できるMethodInfoは値型かそうでないかの判定ができないのでコメントアウト
              //if prePreLast <> Tailcall then
                //failwith "detect tail call but did not emitted tailcall."
          | _ -> ()
      | _ -> invalidOp ""
    #endif

  member this.DeclareLocal(_name: string, typ: Type) =
    let loc = gen.DeclareLocal(typ)
    #if DEVELOPMENT
    loc.SetLocalSymInfo(_name)
    this.WriteLine("// declare local: { val " + _name + ": " + typ.ToReadableText() + " }")
    #endif
    loc

  member this.DeclareTemp(typ: Type) =
    let loc = this.DeclareLocal(sprintf "$tmp_%d" localVariableNumber, typ)
    localVariableNumber <- localVariableNumber + 1
    loc

  member this.BeginExceptionBlock() =
    this.WriteLines([".try"; "{"])
    pushIndent ()
    gen.BeginExceptionBlock()
  member this.BeginCatchBlock(typ: Type) =
    popIndent ()
    this.WriteLines(["}"; "catch " + typ.ToReadableText(); "{"])
    pushIndent ()
    gen.BeginCatchBlock(typ)
  member this.BeginFinallyBlock() =
    popIndent ()
    this.WriteLines(["}"; "finally"; "{"])
    pushIndent ()
    gen.BeginFinallyBlock()
  member this.EndExceptionBlock() =
    popIndent ()
    this.WriteLine("}")
    gen.EndExceptionBlock()
  member this.BeginWhileBlock() =
    this.WriteLine("// while start")
    pushIndent ()
    let loopStart = gen.DefineLabel()
    let loopEnd = gen.DefineLabel()
    this.MarkLabel(loopStart)
    (loopStart, loopEnd)
  member this.EndWhileBlock(loopStart, loopEnd) =
    this.Emit(Br loopStart)
    popIndent ()
    this.WriteLine("// while end")
    this.MarkLabel(loopEnd)
  member this.BeginForBlock() =
    this.WriteLine("// for start")
    pushIndent ()
    let loopStart = gen.DefineLabel()
    let loopEnd = gen.DefineLabel()
    this.MarkLabel(loopStart)
    (loopStart, loopEnd)
  member this.EndForBlock(loopStart, loopEnd) =
    this.Emit(Br loopStart)
    popIndent ()
    this.WriteLine("// for end")
    this.MarkLabel(loopEnd)

  member __.DefineLabel() = gen.DefineLabel()
  member this.MarkLabel(label) =
    this.WriteLine(string (label.GetHashCode()) + ": ")
    gen.MarkLabel(label)

  member __.EmittedOpCodes = emittedOpCodes.AsReadOnly()

  member this.Emit(opcode) =
    emittedOpCodes.Add(opcode)
    let raw = ILOpCode.toRawOpCode opcode
    match opcode with
    | Bgt label | Brfalse label | Br label ->
        this.WriteLineAndMark(raw.Name + " " + string (label.GetHashCode()))
        gen.Emit(raw, label)
    | Leave label ->
        this.WriteLineAndMark(raw.Name)
        gen.Emit(raw, label)
    | Stloc (local, nameOpt) | Ldloc (local, nameOpt) | Ldloca (local, nameOpt) ->
        let hint = match nameOpt with Some name -> "  // " + name | None -> ""
        this.WriteLineAndMark(raw.Name + " " + string local.LocalIndex + hint)
        gen.Emit(raw, local)
    | Stsfld fld | Ldsfld fld | Stfld fld | Ldfld fld | Ldflda fld ->
        this.WriteLineAndMark(raw.Name + " " + (fld.ToReadableText()))
        gen.Emit(raw, fld)
    | Ldstr str ->
        this.WriteLineAndMark(raw.Name + " \"" + str + "\"")
        gen.Emit(raw, str)
    | Ldc_I4_S i | Ldc_I4 i | Ldarg i | Ldarga i | Starg i ->
        this.WriteLineAndMark(raw.Name + " " + string i)
        gen.Emit(raw, i)
    | Ldc_I8 i ->
        this.WriteLineAndMark(raw.Name + " " + string i)
        gen.Emit(raw, i)
    | Ldc_R4 x ->
        this.WriteLineAndMark(raw.Name + " " + x.ToStringWithRichInfo())
        gen.Emit(raw, x)
    | Ldc_R8 x ->
        this.WriteLineAndMark(raw.Name + " " + x.ToStringWithRichInfo())
        gen.Emit(raw, x)
    | Ldtoken tok ->
        match tok with
        | TokType t ->
            this.WriteLineAndMark(raw.Name + " " + t.ToReadableText())
            gen.Emit(raw, t)
        | TokMethod m ->
            this.WriteLineAndMark(raw.Name + " " + m.ToReadableText())
            gen.Emit(raw, m)
        | TokField f ->
            this.WriteLineAndMark(raw.Name + " " + f.ToReadableText())
            gen.Emit(raw, f)
    | Call target | Callvirt target ->
        match target with
        | Method mi ->
            this.WriteLineAndMark(raw.Name + " " + mi.ToReadableText())
            gen.Emit(raw, mi)
        | Ctor ci ->
            this.WriteLineAndMark(raw.Name + " " + ci.ToReadableText())
            gen.Emit(raw, ci)
        | PropGet pi ->
            this.WriteLineAndMark(raw.Name + " " + pi.ToReadableText())
            gen.Emit(raw, pi.GetMethod)
    | Newarr typ | Stelem typ | Initobj typ | Box typ | Unbox_Any typ | Isinst typ | Constrainted typ ->
        this.WriteLineAndMark(raw.Name + " " + typ.ToReadableText())
        gen.Emit(raw, typ)
    | Newobj ci ->
        this.WriteLineAndMark(raw.Name + " " + ci.ToReadableText())
        gen.Emit(raw, ci)
    | And | Or | Xor | Not | Shl | Shr | Shr_Un | Div | Div_Un | Mul | Mul_Ovf | Mul_Ovf_Un | Neg | Rem | Rem_Un | Add | Sub | Sub_Ovf | Sub_Ovf_Un
    | Conv_I | Conv_I1 | Conv_I2 | Conv_I4 | Conv_I8
    | Conv_U | Conv_U1 | Conv_U2 | Conv_U4 | Conv_U8
    | Conv_R_Un | Conv_R4 | Conv_R8
    | Conv_Ovf_I | Conv_Ovf_I1 | Conv_Ovf_I2 | Conv_Ovf_I4 | Conv_Ovf_I8
    | Conv_Ovf_U | Conv_Ovf_U1 | Conv_Ovf_U2 | Conv_Ovf_U4 | Conv_Ovf_U8
    | Conv_Ovf_I_Un | Conv_Ovf_I1_Un | Conv_Ovf_I2_Un | Conv_Ovf_I4_Un | Conv_Ovf_I8_Un
    | Conv_Ovf_U_Un | Conv_Ovf_U1_Un | Conv_Ovf_U2_Un | Conv_Ovf_U4_Un | Conv_Ovf_U8_Un
    | Ldarg_0 | Ldarg_1 | Ldarg_2 | Ldarg_3
    | Ldnull | Ldc_I4_M1 | Ldc_I4_0 | Ldc_I4_1 | Ldc_I4_2 | Ldc_I4_3 | Ldc_I4_4 | Ldc_I4_5 | Ldc_I4_6 | Ldc_I4_7 | Ldc_I4_8
    | Tailcall | Dup | Pop | Rethrow | Ret | Endfinally ->
        this.WriteLineAndMark(raw.Name)
        gen.Emit(raw)

[<AutoOpen>]
module internal GeneratorProvidersExtension =
  open Microsoft.FSharp.Quotations

  let private defineDoc (_name: string) (_builder: ModuleBuilderWrapper) =
    #if DEVELOPMENT
    Some (_builder.RawBuilder.DefineDocument(_name + ".il", SymDocumentType.Text, SymLanguageType.ILAssembly, SymLanguageVendor.Microsoft))
    #else
    None
    #endif

  let methodSig name (args: Var list) (retType: Type) =
    let argsStr = args |> List.map (fun v -> v.Name + ":" + v.Type.ToReadableText()) |> String.concat " -> "
    name + " : " + (if argsStr = "" then "unit" else argsStr) + "  ->  " + retType.ToReadableText()

  type MethodBuilderWrapper with
    member this.GetILGenerator(retType: Type) =
      let gen = this.GetGenerator()
      let doc = defineDoc this.FullName this.ModuleBuilder
      ILGeneratorWrapper.Create(this, methodSig this.Name [] retType, gen, this.FullName, doc)
    member this.GetILGenerator(arg: Var, retType: Type) =
      let gen = this.GetGenerator()
      let doc = defineDoc this.FullName this.ModuleBuilder
      ILGeneratorWrapper.Create(this, methodSig this.Name [arg] retType, gen, this.FullName, doc)

  let ctorSig (varNamesAndTypes: (string * Type) list) =
    let str = varNamesAndTypes |> List.map (fun (n, t) -> n + ":" + t.ToReadableText()) |> String.concat " -> "
    "new : " + str + "  ->  this"

  type CtorBuilderWrapper with
    member this.GetILGenerator(varNamesAndTypes: (string * Type) list) =
      let gen = this.GetGenerator()
      let doc = defineDoc this.FullName this.ModuleBuilder
      ILGeneratorWrapper.Create(this, ctorSig varNamesAndTypes, gen, this.FullName, doc)
