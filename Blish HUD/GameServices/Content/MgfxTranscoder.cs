using System;

namespace Blish_HUD.Content {

    /// <summary>
    /// Compiled effects from MonoGame 3.8.0 (MGFX v9) can't be read by MonoGame 3.8.1+ (MGFX v10).
    /// </summary>
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

    }

}
