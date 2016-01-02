using lndapi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace lndbackup
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            if (args.Length == 5)
            {
                try
                {
                    string Source = args[0];
                    string DestinationRegion = args[1];
                    string DestinationDirectory = args[2];
                    string APIID = args[3];
                    string APIKey = args[4];

                    // Ensure destination directory exists, or program fails right away if it can't be created
                    Directory.CreateDirectory(DestinationDirectory);

                    using (LNDynamic client = new LNDynamic(APIID, APIKey))
                    {
                        List<int> VMIDsToBackup = await GetVMIDsToBackup(client, Source);
                        if (VMIDsToBackup.Count == 0)
                        {
                            Console.WriteLine("Nothing to backup!");
                        }
                        else
                        {
                            Console.WriteLine($"Found {VMIDsToBackup.Count} VMs to backup");
                            foreach (int VMID in VMIDsToBackup)
                            {
                                try
                                {
                                    await HandleBackup(client, VMID, DestinationRegion, DestinationDirectory);
                                }
                                catch (LNDException lndex)
                                {
                                    Console.WriteLine($"lndapi error: {(Debugger.IsAttached ? lndex.ToString() : lndex.Message)}");
                                    Console.WriteLine();
                                    Console.WriteLine("  - Moving on to next VM to backup...");
                                    Console.WriteLine();
                                    Console.WriteLine();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"fatal error: {(Debugger.IsAttached ? ex.ToString() : ex.Message)}");
                                    Console.WriteLine();
                                    Console.WriteLine("  - Moving on to next VM to backup...");
                                    Console.WriteLine();
                                    Console.WriteLine();
                                }
                            }
                        }
                    }
                }
                catch (LNDException lndex)
                {
                    Console.WriteLine($"lndapi error: {(Debugger.IsAttached ? lndex.ToString() : lndex.Message)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"fatal error: {(Debugger.IsAttached ? ex.ToString() : ex.Message)}");
                }

                Console.WriteLine();
                Console.WriteLine("done");
            }
            else
            {
                DisplayUsage();
            }

            if (Debugger.IsAttached) Console.ReadKey();
        }

        private static void DisplayUsage()
        {
            Console.WriteLine();
            Console.WriteLine("USAGE");
            Console.WriteLine();
            Console.WriteLine("     lndbackup <src> <dst_region> <dst_dir> <api_id> <api_key>");
            Console.WriteLine();
            Console.WriteLine("     src             vm_id or region name to backup");
            Console.WriteLine("     dst_region      region name to replicate snapshots to");
            Console.WriteLine("     dst_dir         local path to download snapshots to");
            Console.WriteLine("     api_id          API ID from lndynamic control panel");
            Console.WriteLine("     api_key         API Key from lndynamic control panel");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES");
            Console.WriteLine();
            Console.WriteLine("     lndbackup 12345 toronto c:\\lndbackup api_id api_key");
            Console.WriteLine("     Backup VM with id 12345, replicate to Toronto, download to c:\\lndbackup");
            Console.WriteLine("     (If the VM is provisioned in Toronto, no replication occurs)");
            Console.WriteLine();
            Console.WriteLine("     lndbackup toronto roubaix c:\\lndbackup api_id api_key");
            Console.WriteLine("     Backup all Toronto VMs, replicate to Roubaix, download to c:\\lndbackup");
            Console.WriteLine();
            Console.WriteLine("     lndbackup montreal montreal c:\\lndbackup api_id api_key");
            Console.WriteLine("     Backup all Montreal VMs, don't replicate, download to c:\\lndbackup");
            Console.WriteLine();
            Console.WriteLine("VM_IDs");
            Console.WriteLine();
            Console.WriteLine("     You can find the vm_id for a VM by loading it from the lndynamic control");
            Console.WriteLine("     panel and looking at the querystring.  For example, this is vm_id 12345:");
            Console.WriteLine("     https://dynamic.lunanode.com/panel/vm.php?vm_id=12345");
            Console.WriteLine();
            Console.WriteLine("REGIONS");
            Console.WriteLine();
            Console.WriteLine("     Currently available regions are toronto, montreal, and roubaix");
        }

        private static async Task<List<int>> GetVMIDsToBackup(LNDynamic client, string source)
        {
            List<int> Result = new List<int>();

            if (source.All(Char.IsDigit))
            {
                // Backing up a single vm_id
                Result.Add(int.Parse(source));
            }
            else
            {
                // Backing up an entire region
                var VMs = await client.VMListAsync();
                foreach (var VM in VMs.OrderBy(x => x.vm_id))
                {
                    if (VM.region == source) Result.Add(VM.vm_id);
                }
            }

            return Result;
        }

        private static async Task HandleBackup(LNDynamic client, int vmId, string destinationRegion, string destinationDirectory)
        {
            Console.WriteLine($"- Backing up VMID {vmId}...");
            var Details = await client.VMInfoAsync(vmId);
            Console.WriteLine($"  - hostname: {Details.extra.hostname}");
            Console.WriteLine($"  - region  : {Details.extra.region}");

            // Take a snapshot of the VM
            int SnapshotRetries = 0;
            RetrySnapshot:
            Console.WriteLine($"  - Creating snapshot image...");
            string NewImageName = $"lndbackup {vmId} {DateTime.Now.ToString("yyyy-MM-dd")} {Details.extra.hostname}";
            int NewImageId = await client.VMSnapshotAsync(vmId, NewImageName);
            Console.WriteLine($"    - New snapshot image {NewImageId} queued for creation!");

            // Wait for new image to be 'active'
            try {
                await WaitForImageToBecomeActive(client, NewImageId);
            } catch (LNDImageKilledException) {
                await client.ImageDeleteAsync(NewImageId);
                if (SnapshotRetries++ < 3)
                {
                    Console.WriteLine($"    - Snapshot failed, waiting 30 seconds for retry {SnapshotRetries} of 3");
                    Thread.Sleep(30000);
                    goto RetrySnapshot;
                }
                throw;
            }

            // Replicate image to new region (if necessary)
            if (Details.extra.region != destinationRegion)
            {
                // Replicate the image to the new region
                int ReplicateRetries = 0;
                RetryReplicate:
                Console.WriteLine($"  - Replicating snapshot image to {destinationRegion}...");
                int ReplicatedImageId = await client.ImageReplicateAsync(NewImageId, destinationRegion);
                Console.WriteLine($"    - New snapshot image {ReplicatedImageId} queued for replication!");

                // Wait for new image to be 'active'
                try
                {
                    await WaitForImageToBecomeActive(client, ReplicatedImageId);
                }
                catch (LNDImageKilledException)
                {
                    await client.ImageDeleteAsync(ReplicatedImageId);
                    if (ReplicateRetries++ < 3)
                    {
                        Console.WriteLine($"    - Replication failed, waiting 30 seconds for retry {ReplicateRetries} of 3");
                        Thread.Sleep(30000);
                        goto RetryReplicate;
                    }
                    throw;
                }

                // Delete original image, leaving only replicated image
                Console.WriteLine($"  - Removing original snapshot...");
                await client.ImageDeleteAsync(NewImageId);
                Console.WriteLine($"    - Original snapshot {NewImageId} deleted!");

                NewImageId = ReplicatedImageId;
            }

            // Download new image
            string CleanFilename = string.Join("_", NewImageName.Split(Path.GetInvalidFileNameChars())) + ".img";
            Console.WriteLine($"  - Downloading image {NewImageId} to {Path.Combine(destinationDirectory, CleanFilename)}...");
            await client.ImageRetrieveAsync(NewImageId, Path.Combine(destinationDirectory, CleanFilename), (s, e) =>
            {
                Console.Write($"\r    - Downloaded {e.BytesReceived:n0} of {e.TotalBytesToReceive:n0} bytes ({e.ProgressPercentage:P})...");
            });
            Console.WriteLine();
            Console.WriteLine("    - Image downloaded successfully!");

            // TODO Use client.ImageList to find old lndbackup generated images for this VM and delete them
            // TODO Also delete old lndbackup generated images for this VM from the local filesystem
        }

        private static async Task WaitForImageToBecomeActive(LNDynamic client, int newImageId)
        {
            string NewImageStatus = (await client.ImageDetailsAsync(newImageId)).status;
            while (NewImageStatus != "active")
            {
                if (NewImageStatus == "killed") throw new LNDImageKilledException("image status=killed");

                Console.WriteLine($"      - Image status is '{NewImageStatus}', waiting 30 seconds for 'active'...");
                Thread.Sleep(30000);
                NewImageStatus = (await client.ImageDetailsAsync(newImageId)).status;
            }
            Console.WriteLine("      - Image status is 'active'!");
        }
    }
}
