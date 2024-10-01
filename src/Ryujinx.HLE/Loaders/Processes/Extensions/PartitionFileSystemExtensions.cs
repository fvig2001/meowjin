using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Mods;
using Ryujinx.HLE.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using static LibHac.Result;
using ContentType = LibHac.Ncm.ContentType;

namespace Ryujinx.HLE.Loaders.Processes.Extensions
{
    public static class PartitionFileSystemExtensions
    {
        private static readonly DownloadableContentJsonSerializerContext _contentSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());

        public static Dictionary<ulong, ContentMetaData> GetContentData(this IFileSystem partitionFileSystem,
            ContentMetaType contentType, VirtualFileSystem fileSystem, IntegrityCheckLevel checkLevel)
        {
            fileSystem.ImportTickets(partitionFileSystem);

            var programs = new Dictionary<ulong, ContentMetaData>();

            foreach (DirectoryEntryEx fileEntry in partitionFileSystem.EnumerateEntries("/", "*.cnmt.nca"))
            {
                Cnmt cnmt = partitionFileSystem.GetNca(fileSystem.KeySet, fileEntry.FullPath).GetCnmt(checkLevel, contentType);

                if (cnmt == null)
                {
                    continue;
                }

                ContentMetaData content = new(partitionFileSystem, cnmt);

                if (content.Type != contentType)
                {
                    continue;
                }

                programs.TryAdd(content.ApplicationId, content);
            }

            return programs;
        }

        private static Nca TryOpenNca(IStorage ncaStorage, string containerPath, KeySet deviceKeySet)
        {
            try
            {
                return new Nca(deviceKeySet, ncaStorage);
            }
            catch (Exception ex)
            {

            }

            return null;
        }

        private static List<DownloadableContentContainer> LoadDownloadableContents(string path, VirtualFileSystem _virtualFileSystem, ulong idBase)
        {
            List<DownloadableContentContainer> dlcList = new List<DownloadableContentContainer>();
            using IFileSystem partitionFileSystem = PartitionFileSystemUtils.OpenApplicationFileSystem(path, _virtualFileSystem);
            List<DownloadableContentNca> DownloadableContentNcaList = new List<DownloadableContentNca>();
            DownloadableContentContainer container = new DownloadableContentContainer
            {
                ContainerPath = path,
                DownloadableContentNcaList = new List<DownloadableContentNca>(),
            };
            foreach (DirectoryEntryEx fileEntry in partitionFileSystem.EnumerateEntries("/", "*.nca"))
            {

                using var ncaFile = new UniqueRef<IFile>();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), path, _virtualFileSystem.KeySet);
                if (nca == null)
                {
                    continue;
                }

                if (nca.Header.ContentType == NcaContentType.PublicData)
                {
                    if (nca.GetProgramIdBase() != idBase)
                    {
                        continue;
                    }
                    
                    DownloadableContentNca tmp = new DownloadableContentNca
                    {
                        Enabled = true,
                        TitleId = nca.Header.TitleId,
                        FullPath = fileEntry.FullPath,
                    };

                    container.DownloadableContentNcaList.Add(tmp);

                }
            }

            dlcList.Add(container);
            return dlcList;
        }

        internal static (bool, ProcessResult) TryLoad<TMetaData, TFormat, THeader, TEntry>(this PartitionFileSystemCore<TMetaData, TFormat, THeader, TEntry> partitionFileSystem, Switch device, string path, ulong applicationId, out string errorMessage)
            where TMetaData : PartitionFileSystemMetaCore<TFormat, THeader, TEntry>, new()
            where TFormat : IPartitionFileSystemFormat
            where THeader : unmanaged, IPartitionFileSystemHeader
            where TEntry : unmanaged, IPartitionFileSystemEntry
        {
            errorMessage = null;

            // Load required NCAs.
            Nca mainNca = null;
            Nca patchNca = null;
            Nca controlNca = null;

            try
            {
                Dictionary<ulong, ContentMetaData> applications = partitionFileSystem.GetContentData(ContentMetaType.Application, device.FileSystem, device.System.FsIntegrityCheckLevel);

                if (applicationId == 0)
                {
                    foreach ((ulong _, ContentMetaData content) in applications)
                    {
                        mainNca = content.GetNcaByType(device.FileSystem.KeySet, ContentType.Program, device.Configuration.UserChannelPersistence.Index);
                        controlNca = content.GetNcaByType(device.FileSystem.KeySet, ContentType.Control, device.Configuration.UserChannelPersistence.Index);
                        break;
                    }
                }
                else if (applications.TryGetValue(applicationId, out ContentMetaData content))
                {
                    mainNca = content.GetNcaByType(device.FileSystem.KeySet, ContentType.Program, device.Configuration.UserChannelPersistence.Index);
                    controlNca = content.GetNcaByType(device.FileSystem.KeySet, ContentType.Control, device.Configuration.UserChannelPersistence.Index);
                }

                ProcessLoaderHelper.RegisterProgramMapInfo(device, partitionFileSystem).ThrowIfFailure();
            }
            catch (Exception ex)
            {
                errorMessage = $"Unable to load: {ex.Message}";

                return (false, ProcessResult.Failed);
            }

            if (mainNca != null)
            {
                if (mainNca.Header.ContentType != NcaContentType.Program)
                {
                    errorMessage = "Selected NCA file is not a \"Program\" NCA";

                    return (false, ProcessResult.Failed);
                }

                (Nca updatePatchNca, Nca updateControlNca) = mainNca.GetUpdateData(device.FileSystem, device.System.FsIntegrityCheckLevel, device.Configuration.UserChannelPersistence.Index, out string _, path);

                if (updatePatchNca != null)
                {
                    patchNca = updatePatchNca;
                }

                if (updateControlNca != null)
                {
                    controlNca = updateControlNca;
                }

                // TODO: If we want to support multi-processes in future, we shouldn't clear AddOnContent data here.
                device.Configuration.ContentManager.ClearAocData();

                // Load DownloadableContents.
                string addOnContentMetadataPath = System.IO.Path.Combine(AppDataManager.GamesDirPath, mainNca.GetProgramIdBase().ToString("x16"), "dlc.json");
                string extension = System.IO.Path.GetExtension(path).ToLower();
                bool parseList = File.Exists(addOnContentMetadataPath);
                List<DownloadableContentContainer> dlcContainerList = null;
                if (parseList)
                {
                    dlcContainerList = JsonHelper.DeserializeFromFile(addOnContentMetadataPath, _contentSerializerContext.ListDownloadableContentContainer);
                }
                else if (extension is ".xci")
                {
                    parseList = true;
                    dlcContainerList = LoadDownloadableContents(path, device.FileSystem, mainNca.GetProgramIdBase());
                }

                if (parseList)
                {

                    foreach (DownloadableContentContainer downloadableContentContainer in dlcContainerList)
                    {
                        foreach (DownloadableContentNca downloadableContentNca in downloadableContentContainer.DownloadableContentNcaList)
                        {
                            if (File.Exists(downloadableContentContainer.ContainerPath))
                            {
                                if (downloadableContentNca.Enabled)
                                {
                                    device.Configuration.ContentManager.AddAocItem(downloadableContentNca.TitleId, downloadableContentContainer.ContainerPath, downloadableContentNca.FullPath);
                                }
                            }
                            else
                            {
                                Logger.Warning?.Print(LogClass.Application, $"Cannot find AddOnContent file {downloadableContentContainer.ContainerPath}. It may have been moved or renamed.");
                            }
                        }
                    }
                }

                return (true, mainNca.Load(device, patchNca, controlNca));
            }

            errorMessage = $"Unable to load: Could not find Main NCA for title \"{applicationId:X16}\"";

            return (false, ProcessResult.Failed);
        }

        public static Nca GetNca(this IFileSystem fileSystem, KeySet keySet, string path)
        {
            using var ncaFile = new UniqueRef<IFile>();

            fileSystem.OpenFile(ref ncaFile.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();

            return new Nca(keySet, ncaFile.Release().AsStorage());
        }
    }
}
