using System;
using System.IO;

namespace Blish_HUD.Content {

    // MonoGame 3.8.0 effects (MGFX v9) only differ from 3.8.1+ (MGFX v10) in the width of counts and indices
    // v9:  https://github.com/MonoGame/MonoGame/blob/v3.8/MonoGame.Framework/Graphics/Effect/Effect.cs#L231
    // v10: https://github.com/MonoGame/MonoGame/blob/v3.8.1/MonoGame.Framework/Graphics/Effect/Effect.cs#L235
    // The change: https://github.com/MonoGame/MonoGame/pull/7397
    internal static class MgfxTranscoder {

        private const int MGFX_SIGNATURE = 0x5846474D; // "MGFX"
        private const int HEADER_SIZE    = 10;
        private const int VERSION_OFFSET = 4;

        public const int LEGACY_VERSION  = 9;
        public const int CURRENT_VERSION = 10;

        public static bool IsLegacyEffect(byte[] effectData, int length) {
            return length > HEADER_SIZE
                && BitConverter.ToInt32(effectData, 0) == MGFX_SIGNATURE
                && effectData[VERSION_OFFSET] == LEGACY_VERSION;
        }

        public static bool TryUpgrade(byte[] effectData, int length, out byte[] upgradedData) {
            upgradedData = null;

            if (!IsLegacyEffect(effectData, length)) return false;

            try {
                using var input  = new BinaryReader(new MemoryStream(effectData, 0, length, false));
                using var output = new MemoryStream(length + 256);
                using var writer = new BinaryWriter(output);

                writer.Write(input.ReadBytes(HEADER_SIZE));
                output.Position = VERSION_OFFSET;
                writer.Write((byte)CURRENT_VERSION);
                output.Position = HEADER_SIZE;

                UpgradeEffect(input, writer);

                if (input.BaseStream.Position != input.BaseStream.Length) {
                    throw new InvalidDataException($"Effect has {input.BaseStream.Length - input.BaseStream.Position} unexpected trailing bytes.");
                }

                writer.Write(MGFX_SIGNATURE);
                writer.Flush();

                upgradedData = output.ToArray();
                return true;
            } catch (EndOfStreamException ex) {
                throw new InvalidDataException("Effect ended unexpectedly.", ex);
            }
        }

        private static void UpgradeEffect(BinaryReader input, BinaryWriter output) {
            int constantBufferCount = input.ReadByte();
            output.Write(constantBufferCount);

            for (int c = 0; c < constantBufferCount; c++) {
                CopyString(input, output);
                output.Write(input.ReadInt16()); // Size in bytes

                int parameterCount = input.ReadByte();
                output.Write(parameterCount);

                for (int p = 0; p < parameterCount; p++) {
                    output.Write((int)input.ReadByte()); // Parameter index
                    output.Write(input.ReadUInt16());    // Offset
                }
            }

            int shaderCount = input.ReadByte();
            output.Write(shaderCount);

            for (int s = 0; s < shaderCount; s++) {
                CopyShader(input, output);
            }

            UpgradeParameters(input, output);

            int techniqueCount = input.ReadByte();
            output.Write(techniqueCount);

            for (int t = 0; t < techniqueCount; t++) {
                CopyString(input, output);
                UpgradeAnnotations(input, output);
                UpgradePasses(input, output);
            }
        }

        private static void CopyShader(BinaryReader input, BinaryWriter output) {
            // The shader layout is unchanged between versions, so this only walks it to find where it ends.
            output.Write(input.ReadBoolean()); // Is vertex shader

            int bytecodeLength = input.ReadInt32();
            output.Write(bytecodeLength);
            output.Write(ReadExactly(input, bytecodeLength));

            int samplerCount = input.ReadByte();
            output.Write((byte)samplerCount);

            for (int s = 0; s < samplerCount; s++) {
                output.Write(ReadExactly(input, 3)); // Type, texture slot, sampler slot

                bool hasState = input.ReadBoolean();
                output.Write(hasState);

                if (hasState) {
                    // Address U/V/W, border color, filter, max anisotropy, max mip level, mip LOD bias
                    output.Write(ReadExactly(input, 3 + 4 + 1 + 4 + 4 + 4));
                }

                CopyString(input, output);
                output.Write(input.ReadByte()); // Parameter
            }

            int constantBufferCount = input.ReadByte();
            output.Write((byte)constantBufferCount);
            output.Write(ReadExactly(input, constantBufferCount));

            int attributeCount = input.ReadByte();
            output.Write((byte)attributeCount);

            for (int a = 0; a < attributeCount; a++) {
                CopyString(input, output);
                output.Write(ReadExactly(input, 1 + 1 + 2)); // Usage, index, location
            }
        }

        private static int UpgradeParameters(BinaryReader input, BinaryWriter output) {
            int parameterCount = input.Read7BitEncodedInt();
            output.Write(parameterCount);

            for (int p = 0; p < parameterCount; p++) {
                byte parameterClass = input.ReadByte();
                byte parameterType  = input.ReadByte();
                output.Write(parameterClass);
                output.Write(parameterType);

                CopyString(input, output); // Name
                CopyString(input, output); // Semantic
                UpgradeAnnotations(input, output);

                byte rowCount    = input.ReadByte();
                byte columnCount = input.ReadByte();
                output.Write(rowCount);
                output.Write(columnCount);

                int elementCount = UpgradeParameters(input, output);
                int memberCount  = UpgradeParameters(input, output);

                if (elementCount == 0 && memberCount == 0) {
                    switch (parameterType) {
                        case 1: // Bool
                        case 2: // Int32
                        case 3: // Single
                            output.Write(ReadExactly(input, rowCount * columnCount * 4));
                            break;
                        case 4: // String
                            throw new InvalidDataException("String effect parameters are not supported.");
                    }
                }
            }

            return parameterCount;
        }

        private static void UpgradeAnnotations(BinaryReader input, BinaryWriter output) {
            output.Write((int)input.ReadByte());
        }

        private static void UpgradePasses(BinaryReader input, BinaryWriter output) {
            int passCount = input.ReadByte();
            output.Write(passCount);

            for (int p = 0; p < passCount; p++) {
                CopyString(input, output);
                UpgradeAnnotations(input, output);

                output.Write(ReadShaderIndex(input)); // Vertex shader
                output.Write(ReadShaderIndex(input)); // Pixel shader

                CopyOptionalBlock(input, output, 14 + 4); // Blend state
                CopyOptionalBlock(input, output, 4 + 1 + 1 + 1 + 4 + 1 + 1 + 1 + 1 + 4 + 1 + 4 + 1); // Depth stencil state
                CopyOptionalBlock(input, output, 1 + 4 + 1 + 1 + 1 + 4); // Rasterizer state
            }
        }

        private static int ReadShaderIndex(BinaryReader input) {
            byte index = input.ReadByte();
            return index == byte.MaxValue ? -1 : index;
        }

        private static void CopyOptionalBlock(BinaryReader input, BinaryWriter output, int length) {
            bool present = input.ReadBoolean();
            output.Write(present);

            if (present) {
                output.Write(ReadExactly(input, length));
            }
        }

        private static void CopyString(BinaryReader input, BinaryWriter output) {
            // Strings are length-prefixed the same way in both versions, so the raw bytes are reused.
            int length = input.Read7BitEncodedInt();
            output.Write7BitEncodedInt(length);
            output.Write(ReadExactly(input, length));
        }

        private static byte[] ReadExactly(BinaryReader input, int count) {
            byte[] data = input.ReadBytes(count);

            if (data.Length != count) {
                throw new EndOfStreamException();
            }

            return data;
        }

    }

}
