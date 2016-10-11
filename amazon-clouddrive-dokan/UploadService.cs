﻿namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Newtonsoft.Json;
    using Tools;

    public enum FailReason
    {
        ZeroLength,
        NoResultNode,
        NoFolderNode,
        NoOverwriteNode,
        Conflict,
        Unexpected,
        Cancelled
    }

    public class UploadService : IDisposable
    {
        public const string UploadFolder = "Upload";

        private const int ReuploadDelay = 5000;
        private readonly ConcurrentDictionary<string, UploadInfo> allUploads = new ConcurrentDictionary<string, UploadInfo>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly IHttpCloud cloud;
        private readonly BlockingCollection<UploadInfo> leftUploads = new BlockingCollection<UploadInfo>();
        private readonly int uploadLimit;
        private readonly SemaphoreSlim uploadLimitSemaphore;
        private string cachePath;
        private bool disposedValue; // To detect redundant calls
        private Task serviceTask;

        public UploadService(int limit, IHttpCloud cloud)
        {
            uploadLimit = limit;
            uploadLimitSemaphore = new SemaphoreSlim(limit);
            this.cloud = cloud;
        }

        public delegate void OnUploadFailedDelegate(UploadInfo item, FailReason reason, string message);

        public delegate void OnUploadFinishedDelegate(UploadInfo item, FSItem.Builder amazonNode);

        public delegate void OnUploadProgressDelegate(UploadInfo item, long done);

        public string CachePath
        {
            get
            {
                return cachePath;
            }

            set
            {
                var newpath = Path.Combine(value, UploadFolder, cloud.Id);
                if (cachePath == newpath)
                {
                    return;
                }

                Log.Trace($"Cache path changed from {cachePath} to {newpath}");
                cachePath = newpath;
                Directory.CreateDirectory(cachePath);
                CheckOldUploads();
            }
        }

        public Action<UploadInfo> OnUploadAdded { get; set; }

        public OnUploadFailedDelegate OnUploadFailed { get; set; }

        public OnUploadFinishedDelegate OnUploadFinished { get; set; }

        public OnUploadProgressDelegate OnUploadProgress { get; set; }

        public void AddOverwrite(FSItem item)
        {
            var info = new UploadInfo(item)
            {
                Overwrite = true
            };

            var path = Path.Combine(cachePath, item.Id);
            WriteInfo(path + ".info", info);
            leftUploads.Add(info);
            allUploads.TryAdd(info.Id, info);
            OnUploadAdded?.Invoke(info);
        }

        public void CancelUpload(string id)
        {
            UploadInfo outitem;
            if (allUploads.TryGetValue(id, out outitem))
            {
                outitem.Cancellation.Cancel();
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);

            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }

        public NewFileBlockWriter OpenNew(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            var result = new NewFileBlockWriter(item, path);
            result.OnClose = () =>
              {
                  AddUpload(item);
              };

            return result;
        }

        public NewFileBlockWriter OpenTruncate(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            var result = new NewFileBlockWriter(item, path);
            result.SetLength(0);
            result.OnClose = () =>
            {
                AddOverwrite(item);
            };

            return result;
        }

        public void Start()
        {
            if (serviceTask != null)
            {
                return;
            }

            serviceTask = Task.Factory.StartNew(() => UploadTask(), cancellation.Token, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        public void Stop()
        {
            if (serviceTask == null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                serviceTask.Wait();
            }
            catch (AggregateException e)
            {
                e.Handle(ce => ce is TaskCanceledException);
            }

            serviceTask = null;
        }

        public void WaitForUploadsFinish()
        {
            while (leftUploads.Count > 0)
            {
                Thread.Sleep(100);
            }

            for (int i = 0; i < uploadLimit; i++)
            {
                uploadLimitSemaphore.Wait();
            }

            return;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop();
                    cancellation.Dispose();
                    uploadLimitSemaphore.Dispose();
                    leftUploads.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.
                disposedValue = true;
            }
        }

        private void AddUpload(FSItem item)
        {
            var info = new UploadInfo(item);

            var path = Path.Combine(cachePath, item.Id);
            WriteInfo(path + ".info", info);
            leftUploads.Add(info);
            allUploads.TryAdd(info.Id, info);
            OnUploadAdded?.Invoke(info);
        }

        private void CheckOldUploads()
        {
            var files = Directory.GetFiles(cachePath, "*.info");
            if (files.Length == 0)
            {
                return;
            }

            Log.Warn($"{files.Length} not uploaded files found. Resuming.");
            foreach (var info in files.Select(f => new FileInfo(f)).OrderBy(f => f.CreationTime))
            {
                try
                {
                    var uploadinfo = JsonConvert.DeserializeObject<UploadInfo>(File.ReadAllText(info.FullName));
                    var fileinfo = new FileInfo(Path.Combine(info.DirectoryName, Path.GetFileNameWithoutExtension(info.Name)));
                    var item = FSItem.MakeUploading(uploadinfo.Path, fileinfo.Name, uploadinfo.ParentId, fileinfo.Length);
                    leftUploads.Add(uploadinfo);
                    allUploads.TryAdd(uploadinfo.Id, uploadinfo);
                    OnUploadAdded?.Invoke(uploadinfo);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }
        }

        private void CleanUpload(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception dex)
            {
                Log.Error(dex);
            }

            try
            {
                File.Delete(path + ".info");
            }
            catch (Exception dex)
            {
                Log.Error(dex);
            }
        }

        private async Task Upload(UploadInfo item)
        {
            var path = Path.Combine(cachePath, item.Id);
            try
            {
                if (item.Length == 0)
                {
                    Log.Trace("Zero Length file: " + item.Path);
                    File.Delete(path + ".info");
                    OnUploadFailed(item, FailReason.ZeroLength, null);
                    return;
                }

                Log.Trace("Started upload: " + item.Path);
                FSItem.Builder node;
                if (!item.Overwrite)
                {
                    var checknode = await cloud.Nodes.GetNode(item.ParentId);
                    if (checknode == null || !checknode.IsDir)
                    {
                        Log.Error("Folder does not exist to upload file: " + item.Path);
                        File.Delete(path + ".info");
                        OnUploadFailed(item, FailReason.NoFolderNode, "Parent folder is missing");
                        return;
                    }

                    node = await cloud.Files.UploadNew(
                        item.ParentId,
                        Path.GetFileName(item.Path),
                        () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true),
                        (p) => UploadProgress(item, p));
                }
                else
                {
                    var checknode = await cloud.Nodes.GetNode(item.Id);
                    if (checknode == null)
                    {
                        Log.Error("File does not exist to be overwritten: " + item.Path);
                        File.Delete(path + ".info");
                        OnUploadFailed(item, FailReason.NoOverwriteNode, "No file to overwrite");
                        return;
                    }

                    node = await cloud.Files.Overwrite(
                        item.Id,
                        () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true),
                        (p) => UploadProgress(item, p));
                }

                if (node == null)
                {
                    throw new NullReferenceException("File node is null: " + item.Path);
                }

                File.Delete(path + ".info");

                node.ParentPath = Path.GetDirectoryName(item.Path);
                OnUploadFinished(item, node);
                Log.Trace("Finished upload: " + item.Path + " id:" + node.Id);
                return;
            }
            catch (OperationCanceledException)
            {
                Log.Info("Upload canceled");

                OnUploadFailed(item, FailReason.Cancelled, "Upload cancelled");
                CleanUpload(path);
                return;
            }
            catch (CloudException ex)
            {
                if (ex.Error == System.Net.HttpStatusCode.Conflict)
                {
                    Log.Error($"Upload conflict: {item.Path}\r\n{ex}");
                    var node = await cloud.Nodes.GetChild(item.ParentId, Path.GetFileName(item.Path));
                    if (node != null)
                    {
                        OnUploadFinished(item, node);
                    }
                    else
                    {
                        OnUploadFailed(item, FailReason.Unexpected, "Uploading failed with unexpected Conflict error");
                    }

                    CleanUpload(path);

                    return;
                }

                if (ex.Error == System.Net.HttpStatusCode.NotFound)
                {
                    Log.Error($"Upload error NotFound: {item.Path}\r\n{ex}");
                    OnUploadFailed(item, FailReason.NoFolderNode, "Folder node for new file is not found");

                    CleanUpload(path);

                    return;
                }

                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Upload failed: {item.Path}\r\n{ex}");
                OnUploadFailed(item, FailReason.Unexpected, $"Unexpected Error. Upload will retry.\r\n{ex}");
            }
            finally
            {
                uploadLimitSemaphore.Release();
                UploadInfo outItem;
                allUploads.TryRemove(item.Id, out outItem);
            }

            allUploads.TryAdd(item.Id, item);
            await Task.Delay(ReuploadDelay);
            leftUploads.Add(item);
        }

        private void UploadProgress(UploadInfo item, long p)
        {
            OnUploadProgress?.Invoke(item, p);
            cancellation.Token.ThrowIfCancellationRequested();
            item.Cancellation.Token.ThrowIfCancellationRequested();
        }

        private void UploadTask()
        {
            try
            {
                UploadInfo upload;
                while (leftUploads.TryTake(out upload, -1, cancellation.Token))
                {
                    var uploadCopy = upload;
                    if (!uploadLimitSemaphore.Wait(-1, cancellation.Token))
                    {
                        return;
                    }

                    Task.Run(async () => await Upload(uploadCopy));
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("Upload service stopped");
            }
        }

        private void WriteInfo(string path, UploadInfo info)
        {
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write)))
            {
                writer.Write(JsonConvert.SerializeObject(info));
            }
        }
    }
}