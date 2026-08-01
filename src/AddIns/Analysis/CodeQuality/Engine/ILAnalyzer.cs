// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using Microsoft.CodeAnalysis;

using ICSharpCode.CodeQuality.Engine.Dom;

namespace ICSharpCode.CodeQuality.Engine
{
	/// <summary>
	/// Analyzes method IL for cyclomatic complexity and member-to-member usage edges.
	/// IL is read via System.Reflection.Metadata (PEReader) instead of the old Mono.Cecil stack.
	/// </summary>
	public class ILAnalyzer
	{
		readonly AssemblyAnalyzer context;

		public ILAnalyzer(AssemblyAnalyzer context)
		{
			this.context = context;
		}

		public void Analyze(IMethodSymbol method, NodeBase analyzedNode)
		{
			if (analyzedNode is MethodNode)
				((MethodNode)analyzedNode).CyclomaticComplexity = 0;

			foreach (var body in context.GetMethodBodies(method)) {
				byte[] il = body.GetILBytes();
				if (il == null)
					continue;

				foreach (var instruction in Decode(il)) {
					if (IsBranch(instruction) && analyzedNode is MethodNode)
						((MethodNode)analyzedNode).CyclomaticComplexity++;

					if (instruction.Token != 0 && (IsMethodOperand(instruction.OpCode) || IsFieldOperand(instruction.OpCode))) {
						try {
							context.ResolveTokenTarget(instruction.Token, analyzedNode);
						} catch (Exception) {
							// Malformed metadata / unreadable operand - skip this reference.
						}
					}
				}
			}
		}

		struct InstructionInfo
		{
			public byte OpCode;
			public bool IsTwoByte;
			public int Token;
		}

		static IEnumerable<InstructionInfo> Decode(byte[] il)
		{
			int pos = 0;
			while (pos < il.Length) {
				byte op = il[pos++];
				bool twoByte = false;
				if (op == 0xFE) {
					if (pos >= il.Length)
						yield break;
					op = il[pos++];
					twoByte = true;
				}

				var info = new InstructionInfo { OpCode = op, IsTwoByte = twoByte };
				OperandType operandType = GetOperandType(twoByte, op);
				switch (operandType) {
					case OperandType.None:
						break;
					case OperandType.ShortInlineI:
					case OperandType.ShortInlineBrTarget:
						pos += 1;
						break;
					case OperandType.InlineI:
					case OperandType.InlineBrTarget:
					case OperandType.InlineString:
					case OperandType.InlineSig:
					case OperandType.InlineType:
					case OperandType.InlineField:
					case OperandType.InlineMethod:
					case OperandType.InlineTok:
						if (pos + 4 <= il.Length) {
							info.Token = BitConverter.ToInt32(il, pos);
						}
						pos += 4;
						break;
					case OperandType.InlineI8:
					case OperandType.InlineR:
						pos += 8;
						break;
					case OperandType.ShortInlineR:
						pos += 4;
						break;
					case OperandType.InlineVar:
						pos += 2;
						break;
					case OperandType.InlineSwitch:
						if (pos + 4 <= il.Length) {
							int count = BitConverter.ToInt32(il, pos);
							pos += 4 + count * 4;
						} else {
							pos = il.Length;
						}
						break;
					default:
						yield break;
				}
				yield return info;
			}
		}

		enum OperandType
		{
			None,
			ShortInlineI,
			InlineI,
			InlineI8,
			ShortInlineR,
			InlineR,
			ShortInlineBrTarget,
			InlineBrTarget,
			InlineSwitch,
			InlineVar,
			InlineString,
			InlineSig,
			InlineType,
			InlineField,
			InlineMethod,
			InlineTok
		}

		static OperandType GetOperandType(bool twoByte, byte op)
		{
			if (!twoByte) {
				// Single-byte opcodes that carry a metadata token operand.
				switch (op) {
					case 0x28: return OperandType.InlineMethod; // call
					case 0x6F: return OperandType.InlineMethod; // callvirt
					case 0x73: return OperandType.InlineMethod; // newobj
					case 0x7B: return OperandType.InlineField;  // ldfld
					case 0x7C: return OperandType.InlineField;  // ldflda
					case 0x7D: return OperandType.InlineField;  // stfld
					case 0x7E: return OperandType.InlineField;  // ldsfld
					case 0x7F: return OperandType.InlineField;  // ldsflda
					case 0x80: return OperandType.InlineField;  // stsfld
					case 0x72: return OperandType.InlineString; // ldstr
					case 0xD0: return OperandType.InlineMethod; // ldftn
					case 0xD2: return OperandType.InlineMethod; // ldvirtftn
					case 0xD1: return OperandType.InlineType;   // ldtoken
					case 0xFE: return OperandType.None;
					default: break;
				}
				// Branches (single-byte forms).
				if (op >= 0x2B && op <= 0x44)
					return OperandType.InlineBrTarget;
				if (op == 0x45) // switch
					return OperandType.InlineSwitch;
				// Short branches (0x2B..0x3F without "s"? no - 0x2B-0x3F are short, 0x3C-0x44 long):
				if (op >= 0x2B && op <= 0x37)
					return OperandType.ShortInlineBrTarget;
				if (op == 0x38) // br
					return OperandType.InlineBrTarget;
				if (op == 0x39 || op == 0x3A || op == 0x3B) // brfalse/brtrue/brfalse.s? handled above
					return OperandType.InlineBrTarget;
				// Fixed-size operands.
				switch (op) {
					case 0x1F: return OperandType.ShortInlineI;  // ldc.i4.s
					case 0x20: return OperandType.InlineI;       // ldc.i4
					case 0x21: return OperandType.InlineI8;      // ldc.i8
					case 0x22: return OperandType.ShortInlineR;  // ldc.r4
					case 0x23: return OperandType.InlineR;       // ldc.r8
					case 0x11: return OperandType.ShortInlineI;  // ldc.i4.s alias
					case 0x0E: return OperandType.ShortInlineI;  // ldc.i4.s alias
					default: break;
				}
				return OperandType.None;
			}

			// Two-byte (0xFE-prefixed) opcodes.
			switch (op) {
				case 0x09: return OperandType.InlineMethod; // calli (sig token)
				case 0x06: return OperandType.InlineMethod; // ldftn (0xFE06)
				case 0x07: return OperandType.InlineMethod; // ldvirtftn (0xFE07)
				case 0x0C: return OperandType.InlineType;   // ldtoken (0xFE0C)
				case 0x11: return OperandType.InlineField;  // ldflda (0xFE11)? - no, ldflda is 0x7C; 0xFE11 is "ldloca" (short var)
				default: break;
			}
			return OperandType.None;
		}

		static bool IsBranch(byte opCode, bool twoByte)
		{
			if (!twoByte) {
				return (opCode >= 0x2B && opCode <= 0x44) || opCode == 0x45;
			}
			return false;
		}

		static bool IsBranch(InstructionInfo info)
		{
			return IsBranch(info.OpCode, info.IsTwoByte);
		}

		static bool IsMethodOperand(byte opCode)
		{
			return opCode == 0x28 || opCode == 0x6F || opCode == 0x73 || opCode == 0xD0 || opCode == 0xD2;
		}

		static bool IsFieldOperand(byte opCode)
		{
			return opCode >= 0x7B && opCode <= 0x80;
		}
	}
}
