using System.IO;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk
{
    public class AddDirectoryChunk : ZiPatchChunk
    {
        public new static string Type = "ADIR";

        public string DirName { get; protected set; }

        protected override void ReadChunk()
        {
            var start = this.Reader.BaseStream.Position;

            var dirNameLen = this.Reader.ReadUInt32BE();

            DirName = this.Reader.ReadFixedLengthString(dirNameLen);

            this.Reader.ReadBytes(Size - (int)(this.Reader.BaseStream.Position - start));
        }


        public AddDirectoryChunk(ChecksumBinaryReader reader, int offset, int size) : base(reader, offset, size) {}

        public override void ApplyChunk(ZiPatchConfig config)
        {
            Directory.CreateDirectory(WebLaunch.Core.SafePath.Resolve(config.GamePath, DirName.StartsWith('/') ? DirName[1..] : DirName));
        }

        public override string ToString()
        {
            return $"{Type}:{DirName}";
        }
    }
}