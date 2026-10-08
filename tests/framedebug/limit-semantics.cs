// Read-only runtime IL evidence; no window/enable/GUI calls.
var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow");
var method = type.GetMethod("ChangeFrameEventLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null);
var body = method.GetMethodBody().GetILAsByteArray();
var codes = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(f => f.FieldType == typeof(System.Reflection.Emit.OpCode))
    .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)).ToDictionary(op => op.Value);
var lines = new List<string>();
for (int i = 0; i < body.Length;) {
    int offset = i;
    short key = body[i++];
    if (key == 0xfe) key = (short)(0xfe00 | body[i++]);
    var op = codes[key];
    string operand = "";
    switch (op.OperandType) {
        case System.Reflection.Emit.OperandType.InlineNone: break;
        case System.Reflection.Emit.OperandType.ShortInlineBrTarget: operand = ((sbyte)body[i] + i + 1).ToString(); i++; break;
        case System.Reflection.Emit.OperandType.ShortInlineI:
        case System.Reflection.Emit.OperandType.ShortInlineVar: operand = body[i++].ToString(); break;
        case System.Reflection.Emit.OperandType.InlineVar: operand = BitConverter.ToUInt16(body, i).ToString(); i += 2; break;
        case System.Reflection.Emit.OperandType.InlineBrTarget: operand = (BitConverter.ToInt32(body, i) + i + 4).ToString(); i += 4; break;
        case System.Reflection.Emit.OperandType.InlineMethod:
        case System.Reflection.Emit.OperandType.InlineField:
        case System.Reflection.Emit.OperandType.InlineType:
        case System.Reflection.Emit.OperandType.InlineTok: operand = method.Module.ResolveMember(BitConverter.ToInt32(body, i)).ToString(); i += 4; break;
        case System.Reflection.Emit.OperandType.InlineString: operand = method.Module.ResolveString(BitConverter.ToInt32(body, i)); i += 4; break;
        case System.Reflection.Emit.OperandType.InlineI: operand = BitConverter.ToInt32(body, i).ToString(); i += 4; break;
        case System.Reflection.Emit.OperandType.InlineI8: operand = BitConverter.ToInt64(body, i).ToString(); i += 8; break;
        case System.Reflection.Emit.OperandType.ShortInlineR: operand = BitConverter.ToSingle(body, i).ToString(); i += 4; break;
        case System.Reflection.Emit.OperandType.InlineR: operand = BitConverter.ToDouble(body, i).ToString(); i += 8; break;
        default: throw new InvalidOperationException("Unexpected operand " + op.OperandType);
    }
    lines.Add(offset + ": " + op.Name + " " + operand);
}
return new Dictionary<string, object> { ["unity"] = Application.unityVersion, ["method"] = method.ToString(), ["assembly"] = method.Module.Name, ["il"] = lines };
