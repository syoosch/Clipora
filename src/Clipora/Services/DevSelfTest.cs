using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Common;
using Clipora.Interop;
using Clipora.Models;
using Clipora.Scrolling;
using Clipora.ViewModels;
using Clipora.Views;
using Microsoft.Win32;

namespace Clipora.Services;

/// <summary>
/// 开发期数据层自检：在临时目录验证建库、增删改查、去重、排序、筛选、标签、回收站、设置读写。
/// 通过命令行 <c>--selftest</c> 触发，结果写入 <c>%TEMP%\clipora-selftest-result.txt</c> 并以退出码反映。
/// </summary>
public static class DevSelfTest
{
    public static int RunImageThumbnailContainer()
    {
        string resultPath = Path.Combine(Path.GetTempPath(), "clipora-thumbnail-selftest-result.txt");
        string dir = Path.Combine(Path.GetTempPath(), "clipora-thumbnail-selftest-" + Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new AppPaths(dir);
            var settings = new SettingsService(paths);
            RunImageThumbnailContainerTests(paths, settings);
            File.WriteAllText(resultPath, "THUMBNAIL SELFTEST OK");
            Console.WriteLine("THUMBNAIL SELFTEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "THUMBNAIL SELFTEST FAIL: " + ex);
            Console.WriteLine("THUMBNAIL SELFTEST FAIL: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static int Run()
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "clipora-selftest-result.txt");
        var dir = Path.Combine(Path.GetTempPath(), "clipora-selftest-" + Guid.NewGuid().ToString("N"));

        // M6.1a: 先清理过期残留（避免 %TEMP% 无限堆积）
        CleanLegacyTempFiles();

        try
        {
            var paths = new AppPaths(dir);
            var db = new Database(paths);
            var clips = new SqliteClipStore(db);
            var tags = new SqliteTagStore(db);
            var settings = new SettingsService(paths);

            DateTime firstCaptureAt = new DateTime(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc);
            DateTime repeatedCaptureAt = firstCaptureAt.AddMinutes(5);
            long id1 = clips.Add(new ClipItem { Type = ClipType.Text, PreviewText = "hello", TextContent = "hello world", ContentHash = "h1", SizeBytes = 11, CreatedAt = firstCaptureAt }, true);
            long id2 = clips.Add(new ClipItem { Type = ClipType.Url, PreviewText = "https://x", TextContent = "https://x", ContentHash = "h2", SizeBytes = 9 }, true);

            long id1b = clips.Add(new ClipItem { Type = ClipType.Text, PreviewText = "hello", TextContent = "hello world", ContentHash = "h1", SizeBytes = 11, CreatedAt = repeatedCaptureAt }, true);
            Assert(id1b == id1, "去重应返回已有 Id");
            Assert(clips.Query(new ClipQuery()).Count == 2, "去重后应仍为 2 条");
            Assert(clips.GetById(id1)!.CreatedAt == repeatedCaptureAt,
                "外部重复复制/拖入经过 Add 去重时应刷新 CreatedAt");

            clips.SetPinned(id2, true);
            Assert(clips.Query(new ClipQuery())[0].Id == id2, "置顶项应排在最前");

            Assert(clips.Query(new ClipQuery { Type = ClipType.Url }).Count == 1, "类型筛选");
            var found = clips.Query(new ClipQuery { Search = "world" });
            Assert(found.Count == 1 && found[0].Id == id1, "关键词搜索");

            string richPath = Path.Combine(paths.RichTextDir, "sample.rtf");
            File.WriteAllText(richPath, @"{\rtf1\b hello\b0}");
            clips.Add(new ClipItem
            {
                Type = ClipType.RichText,
                PreviewText = "hello",
                TextContent = "hello",
                RefPath = richPath,
                ContentHash = "rich1",
                SizeBytes = 20,
            }, true);
            Assert(clips.Query(new ClipQuery { Type = ClipType.RichText }).Count == 1, "富文本类型持久化");

            string sampleFile = Path.Combine(paths.FilePayloadsDir, "sample.txt");
            File.WriteAllText(sampleFile, "file payload");
            string manifestPath = Path.Combine(paths.FileManifestsDir, "sample.clipora-files.json");
            var manifest = new ClipFileManifest
            {
                Entries =
                {
                    new ClipFileManifestEntry
                    {
                        OriginalPath = sampleFile,
                        StoredPath = sampleFile,
                        DisplayName = "sample.txt",
                        SizeBytes = new FileInfo(sampleFile).Length,
                    },
                },
            };
            manifest.Save(manifestPath);
            Assert(ClipFileManifest.Load(manifestPath)?.GetAvailablePaths().Count == 1, "文件清单读写");
            long fileId = clips.Add(new ClipItem
            {
                Type = ClipType.File,
                PreviewText = "文件 sample.txt",
                RefPath = manifestPath,
                ContentHash = "file1",
                SizeBytes = new FileInfo(sampleFile).Length,
            }, true);
            Assert(clips.GetById(fileId)?.RefPath == manifestPath, "文件类型持久化");
            RunClipboardRoundTripWithRetry(paths, settings, clips);

            long tagId = tags.Create("工作", "#0078D4");
            tags.Assign(id1, tagId);
            Assert(tags.GetTagIds(id1) is { Count: 1 } t && t[0] == tagId, "标签关联");
            Assert(clips.Query(new ClipQuery { TagId = tagId }).Count == 1, "标签筛选");
            tags.Delete(tagId);
            Assert(tags.GetTagIds(id1).Count == 0, "删除标签应级联解除关联");

            int activeCountBeforeDelete = clips.Query(new ClipQuery()).Count;
            clips.SoftDelete(id1);
            Assert(clips.Query(new ClipQuery()).Count == activeCountBeforeDelete - 1, "软删除应从常规视图隐藏");
            Assert(clips.Query(new ClipQuery { IncludeDeleted = true }).Count == 1, "回收站应显示已删除项");
            clips.Restore(id1);
            Assert(clips.Query(new ClipQuery()).Count == activeCountBeforeDelete, "恢复");

            // 设置读写往返
            settings.Current.RetentionDays = 7;
            settings.Current.HotkeyOpenPanel = "Alt+C";
            settings.Current.AlwaysOnTop = true;
            settings.Current.WindowWidth = 420;
            settings.Current.WindowHeight = 580;
            settings.Current.WindowLeft = 120;
            settings.Current.WindowTop = 80;
            settings.Save();
            var reloaded = new SettingsService(paths);
            Assert(reloaded.Current.RetentionDays == 7
                && reloaded.Current.HotkeyOpenPanel == "Alt+C"
                && reloaded.Current.AlwaysOnTop
                && reloaded.Current.WindowWidth == 420
                && reloaded.Current.WindowHeight == 580
                && reloaded.Current.WindowLeft == 120
                && reloaded.Current.WindowTop == 80,
                "设置持久化");

            // —— SettingsViewModel 绑定 + 即时保存 ——
            var settingsStub = new SelfTestSettingsService();
            var autoStartTest = new AutoStartService("Clipora_SelfTest");
            var svm = new SettingsViewModel(settingsStub, autoStartTest, dir);

            svm.AutoPasteOnUse = false;
            Assert(!settingsStub.Current.AutoPasteOnUse && settingsStub.SaveCount >= 1,
                "SettingsViewModel.AutoPasteOnUse setter 应即时写盘");

            svm.MergeDuplicates = false;
            Assert(!settingsStub.Current.MergeDuplicates && settingsStub.SaveCount >= 2,
                "SettingsViewModel.MergeDuplicates setter 应即时写盘");

            svm.SilentStart = true;
            Assert(settingsStub.Current.SilentStart && settingsStub.SaveCount >= 3,
                "SettingsViewModel.SilentStart setter 应即时写盘");

            svm.MinimizeTo = MinimizeBehavior.Tray;
            Assert(settingsStub.Current.MinimizeTo == MinimizeBehavior.Tray && settingsStub.SaveCount >= 4,
                "SettingsViewModel.MinimizeTo setter 应即时写盘");

            svm.CloseTo = CloseBehavior.Exit;
            Assert(settingsStub.Current.CloseTo == CloseBehavior.Exit && settingsStub.SaveCount >= 5,
                "SettingsViewModel.CloseTo setter 应即时写盘");

            // —— AutoStartService 注册表往返 ——
            Assert(autoStartTest.TrySetEnabled(true, out string? enableError) && enableError is null,
                "AutoStartService.TrySetEnabled(true) 应成功写入注册表");
            Assert(autoStartTest.IsEnabled(),
                "AutoStartService.IsEnabled() 写入后应读回 true");
            Assert(svm.AutoStart,
                "SettingsViewModel.AutoStart getter 应从注册表读回 true");

            svm.AutoStart = false;
            Assert(!autoStartTest.IsEnabled(),
                "SettingsViewModel.AutoStart setter(false) 应移除注册表项");
            Assert(!settingsStub.Current.AutoStart,
                "AutoStart setter 成功时应同步 Current.AutoStart");

            // 清理自检注册表值
            autoStartTest.TrySetEnabled(false, out _);

            // —— SettingsViewModel 存储属性绑定 + 输入归一化（M4.2.2b） ——
            var storageStub = new SelfTestSettingsService();
            string testDir = Path.Combine(Path.GetTempPath(), "clipora-selftest-data");
            var svmStorage = new SettingsViewModel(storageStub, autoStartTest, testDir);

            // 构造默认值
            Assert(svmStorage.RetentionDays == 3,
                "SettingsViewModel 构造后 RetentionDays 默认为 3");
            Assert(svmStorage.MaxItemMegabytes == 25d,
                "SettingsViewModel 构造后 MaxItemMegabytes 默认为 25");
            Assert(svmStorage.DataDirectory == Path.GetFullPath(testDir),
                "SettingsViewModel DataDirectory 应等于构造注入路径");

            // RetentionDays 合法变更
            int saveBefore = storageStub.SaveCount;
            svmStorage.RetentionDays = 7;
            Assert(storageStub.Current.RetentionDays == 7,
                "RetentionDays=7 应写入模型");
            Assert(storageStub.SaveCount == saveBefore + 1,
                "RetentionDays 合法变更应恰增加一次保存");

            // 同值不重复保存
            svmStorage.RetentionDays = 7;
            Assert(storageStub.SaveCount == saveBefore + 1,
                "RetentionDays 设同值不应重复保存");

            // RetentionDays=0（永久）
            svmStorage.RetentionDays = 0;
            Assert(storageStub.Current.RetentionDays == 0,
                "RetentionDays=0 应写入模型");
            Assert(storageStub.SaveCount == saveBefore + 2,
                "RetentionDays=0 应恰增加一次保存");

            // 非法值不改模型、不保存
            int saveBeforeInvalid = storageStub.SaveCount;
            int retentionBeforeInvalid = storageStub.Current.RetentionDays;
            svmStorage.RetentionDays = 2;
            Assert(storageStub.Current.RetentionDays == retentionBeforeInvalid,
                "RetentionDays=2（非法值）不应改模型");
            Assert(storageStub.SaveCount == saveBeforeInvalid,
                "RetentionDays 非法值不应保存");

            // MaxItemMegabytes 合法变更
            int saveMb = storageStub.SaveCount;
            svmStorage.MaxItemMegabytes = 64;
            Assert(storageStub.Current.MaxItemBytes == 64L * 1024 * 1024,
                "MaxItemMegabytes=64 应写入精确字节数");
            Assert(storageStub.SaveCount == saveMb + 1,
                "MaxItemMegabytes 合法变更应恰增加一次保存");

            // 同值不重复保存
            svmStorage.MaxItemMegabytes = 64;
            Assert(storageStub.SaveCount == saveMb + 1,
                "MaxItemMegabytes 设同值不应重复保存");

            // clamp：0 → 1 MiB
            int saveClamp = storageStub.SaveCount;
            svmStorage.MaxItemMegabytes = 0;
            Assert(storageStub.Current.MaxItemBytes == 1L * 1024 * 1024,
                "MaxItemMegabytes=0 应 clamp 为 1 MiB");
            Assert(svmStorage.MaxItemMegabytes == 1d,
                "clamp 为 1 MiB 后 getter 应返回 1");

            // clamp：2048 → 1024 MiB
            svmStorage.MaxItemMegabytes = 2048;
            Assert(storageStub.Current.MaxItemBytes == 1024L * 1024 * 1024,
                "MaxItemMegabytes=2048 应 clamp 为 1024 MiB");

            // AwayFromZero 归一：25.5 → 26 MiB
            svmStorage.MaxItemMegabytes = 25.5;
            Assert(storageStub.Current.MaxItemBytes == 26L * 1024 * 1024,
                $"MaxItemMegabytes=25.5 应按 AwayFromZero 归一为 26 MiB（实际字节={storageStub.Current.MaxItemBytes}，期望={26L * 1024 * 1024}）");

            // NaN 不改模型、不保存、不抛异常
            int saveBeforeNaN = storageStub.SaveCount;
            long bytesBeforeNaN = storageStub.Current.MaxItemBytes;
            svmStorage.MaxItemMegabytes = double.NaN;
            Assert(storageStub.Current.MaxItemBytes == bytesBeforeNaN,
                "MaxItemMegabytes=NaN 不应改模型");
            Assert(storageStub.SaveCount == saveBeforeNaN,
                "MaxItemMegabytes=NaN 不应保存");

            // +Infinity 不改模型、不保存
            svmStorage.MaxItemMegabytes = double.PositiveInfinity;
            Assert(storageStub.Current.MaxItemBytes == bytesBeforeNaN,
                "MaxItemMegabytes=+∞ 不应改模型");

            // -Infinity 不改模型、不保存
            svmStorage.MaxItemMegabytes = double.NegativeInfinity;
            Assert(storageStub.Current.MaxItemBytes == bytesBeforeNaN,
                "MaxItemMegabytes=-∞ 不应改模型");

            // —— Fluent custom background (M8) ——
            RunCustomBackgroundSettingsViewModelTests(dir, autoStartTest);

            // —— ClipFileReferenceValidator 惰性检测（M4.2.2c） ——
            string refDir = Path.Combine(dir, "ref-test");
            Directory.CreateDirectory(refDir);

            // 1. 单文件仅引用存在 → 验证有效
            string singleFile = Path.Combine(refDir, "single.txt");
            File.WriteAllText(singleFile, "reference file");
            string singleManifestPath = Path.Combine(refDir, "single.clipora-files.json");
            new ClipFileManifest
            {
                IsReferenceOnly = true,
                Entries =
                {
                    new ClipFileManifestEntry { OriginalPath = singleFile, DisplayName = "single.txt" },
                },
            }.Save(singleManifestPath);
            var singleClip = new ClipItem { Type = ClipType.File, PreviewText = "single.txt（仅引用）", RefPath = singleManifestPath };
            ClipFileReferenceValidation singleResult = ClipFileReferenceValidator.Validate(singleClip);
            Assert(singleResult.IsReferenceOnly && singleResult.IsValid,
                "单文件仅引用存在：应报告 IsReferenceOnly=true, IsValid=true");
            Assert(singleResult.Paths.Count == 1 && singleResult.Paths[0] == singleFile,
                "单文件仅引用存在：Paths 应包含 OriginalPath");
            Assert(ClipFileManifest.Load(singleManifestPath)?.IsReferenceInvalid != true,
                "单文件仅引用存在：manifest IsReferenceInvalid 应为 false");

            // 2. 删除原文件 → 无效，manifest 持久化 true；VM 构造即显示"已失效"
            File.Delete(singleFile);
            ClipFileReferenceValidation missingResult = ClipFileReferenceValidator.Validate(singleClip);
            Assert(missingResult.IsReferenceOnly && !missingResult.IsValid,
                "删除原文件后：应报告 IsReferenceOnly=true, IsValid=false");
            Assert(missingResult.Paths.Count == 0,
                "删除原文件后：Paths 应为空");
            Assert(ClipFileManifest.Load(singleManifestPath)?.IsReferenceInvalid == true,
                "删除原文件后：manifest IsReferenceInvalid 应持久化为 true");
            var missingVm = new ClipItemViewModel(singleClip);
            Assert(missingVm.IsReferenceInvalid,
                "删除原文件后构造 VM：IsReferenceInvalid 应为 true");
            Assert(missingVm.ReferenceStatusText == "已失效",
                $"删除原文件后构造 VM：ReferenceStatusText 应为'已失效'（实际='{missingVm.ReferenceStatusText}'）");

            // 3. 恢复同路径文件 → 有效，manifest 持久化 false；VM SetReferenceInvalid(false) 恢复
            File.WriteAllText(singleFile, "recovered");
            ClipFileReferenceValidation recoveredResult = ClipFileReferenceValidator.Validate(singleClip);
            Assert(recoveredResult.IsReferenceOnly && recoveredResult.IsValid,
                "恢复文件后：应报告 IsValid=true");
            Assert(ClipFileManifest.Load(singleManifestPath)?.IsReferenceInvalid != true,
                "恢复文件后：manifest IsReferenceInvalid 应持久化为 false");
            missingVm.SetReferenceInvalid(false);
            Assert(!missingVm.IsReferenceInvalid,
                "SetReferenceInvalid(false) 后：IsReferenceInvalid 应为 false");
            Assert(missingVm.ReferenceStatusText == "仅引用",
                $"恢复后 VM ReferenceStatusText 应为'仅引用'（实际='{missingVm.ReferenceStatusText}'）");

            // 4. 目录仅引用存在/删除
            string singleDir = Path.Combine(refDir, "single-dir");
            Directory.CreateDirectory(singleDir);
            string dirManifestPath = Path.Combine(refDir, "dir.clipora-files.json");
            new ClipFileManifest
            {
                IsReferenceOnly = true,
                Entries =
                {
                    new ClipFileManifestEntry { OriginalPath = singleDir, DisplayName = "single-dir", IsDirectory = true },
                },
            }.Save(dirManifestPath);
            var dirClip = new ClipItem { Type = ClipType.File, PreviewText = "single-dir（仅引用）", RefPath = dirManifestPath };
            Assert(ClipFileReferenceValidator.Validate(dirClip).IsValid,
                "目录仅引用存在：应报告 IsValid=true");
            Directory.Delete(singleDir);
            Assert(!ClipFileReferenceValidator.Validate(dirClip).IsValid,
                "删除目录后：应报告 IsValid=false");

            // 5. 多文件仅引用一项缺失 → 整体无效且 Paths 为空
            string mf1 = Path.Combine(refDir, "multi-a.txt");
            string mf2 = Path.Combine(refDir, "multi-b.txt");
            File.WriteAllText(mf1, "a");
            File.WriteAllText(mf2, "b");
            string multiManifestPath = Path.Combine(refDir, "multi.clipora-files.json");
            new ClipFileManifest
            {
                IsReferenceOnly = true,
                Entries =
                {
                    new ClipFileManifestEntry { OriginalPath = mf1, DisplayName = "multi-a.txt" },
                    new ClipFileManifestEntry { OriginalPath = mf2, DisplayName = "multi-b.txt" },
                },
            }.Save(multiManifestPath);
            var multiClip = new ClipItem { Type = ClipType.File, PreviewText = "2 files（仅引用）", RefPath = multiManifestPath };
            Assert(ClipFileReferenceValidator.Validate(multiClip).IsValid,
                "多文件仅引用全部存在：应报告 IsValid=true");
            File.Delete(mf2);
            ClipFileReferenceValidation multiMissing = ClipFileReferenceValidator.Validate(multiClip);
            Assert(!multiMissing.IsValid && multiMissing.Paths.Count == 0,
                "多文件仅引用一项缺失：整体应无效且 Paths 为空");
            // 恢复全部
            File.WriteAllText(mf2, "b");
            Assert(ClipFileReferenceValidator.Validate(multiClip).IsValid,
                "多文件全部恢复后：应报告 IsValid=true");

            // 6. manifest 缺失/损坏/空条目/空 OriginalPath → fail-closed
            string missingManifest = Path.Combine(refDir, "missing.clipora-files.json");
            var missingManifestClip = new ClipItem { Type = ClipType.File, PreviewText = "missing（仅引用）", RefPath = missingManifest };
            ClipFileReferenceValidation noManifest = ClipFileReferenceValidator.Validate(missingManifestClip);
            Assert(noManifest.IsReferenceOnly && !noManifest.IsValid,
                "manifest 缺失：应 fail-closed 为无效");

            string corruptPath = Path.Combine(refDir, "corrupt.clipora-files.json");
            File.WriteAllText(corruptPath, "{this is not json");
            var corruptClip = new ClipItem { Type = ClipType.File, PreviewText = "corrupt（仅引用）", RefPath = corruptPath };
            Assert(!ClipFileReferenceValidator.Validate(corruptClip).IsValid,
                "manifest 损坏：应 fail-closed");

            string emptyPath = Path.Combine(refDir, "empty.clipora-files.json");
            new ClipFileManifest { IsReferenceOnly = true, Entries = new() }.Save(emptyPath);
            var emptyClip = new ClipItem { Type = ClipType.File, PreviewText = "empty（仅引用）", RefPath = emptyPath };
            Assert(!ClipFileReferenceValidator.Validate(emptyClip).IsValid,
                "manifest 空条目：应 fail-closed");

            string blankOrigPath = Path.Combine(refDir, "blank-orig.clipora-files.json");
            new ClipFileManifest
            {
                IsReferenceOnly = true,
                Entries = { new ClipFileManifestEntry { OriginalPath = "", DisplayName = "blank" } },
            }.Save(blankOrigPath);
            var blankOrigClip = new ClipItem { Type = ClipType.File, PreviewText = "blank（仅引用）", RefPath = blankOrigPath };
            Assert(!ClipFileReferenceValidator.Validate(blankOrigClip).IsValid,
                "空 OriginalPath：应 fail-closed");

            // 7. 非仅引用副本项 → 验证器不探测/不标失效
            string copyFile = Path.Combine(refDir, "copy-payload.txt");
            File.WriteAllText(copyFile, "copy content");
            string copyManifestPath = Path.Combine(refDir, "copy.clipora-files.json");
            new ClipFileManifest
            {
                IsReferenceOnly = false,
                Entries =
                {
                    new ClipFileManifestEntry { OriginalPath = Path.Combine(refDir, "original.txt"), StoredPath = copyFile, DisplayName = "copy.txt" },
                },
            }.Save(copyManifestPath);
            var copyClip = new ClipItem { Type = ClipType.File, PreviewText = "copy.txt", RefPath = copyManifestPath };
            ClipFileReferenceValidation copyResult = ClipFileReferenceValidator.Validate(copyClip);
            Assert(!copyResult.IsReferenceOnly,
                "非仅引用副本项：IsReferenceOnly 应为 false");
            Assert(copyResult.IsValid,
                "非仅引用副本项：不应探测路径，直接返回 valid");

            // 8. ClipWriter 失效结果 = ReferenceUnavailable，不写剪贴板
            var refWriter = new ClipWriter();
            // 用缺失文件的仅引用项
            ClipWriteResult refUnavailResult = refWriter.Write(missingManifestClip);
            Assert(refUnavailResult == ClipWriteResult.ReferenceUnavailable,
                "ClipWriter 对失效仅引用项应返回 ReferenceUnavailable");

            // 有效仅引用写回应成功
            var refWriterValid = new ClipWriter();
            ClipWriteResult refValidResult = refWriterValid.Write(singleClip);
            Assert(refValidResult == ClipWriteResult.Completed,
                "ClipWriter 对有效仅引用项应返回 Completed");

            // 9. MainViewModel.Use 集成
            var useClips = new SqliteClipStore(new Database(paths));
            var useTags = new SqliteTagStore(new Database(paths));
            var useMonitor = new SelfTestMonitor();
            var useWriter = new ClipWriter();
            var useVm = new MainViewModel(useClips, useTags, useMonitor, useWriter);

            // 失效项测试：缺失 manifest 的仅引用 → VM 初始不标记（manifest 不存在），Use 时 Validator 检测到
            long useId = useClips.Add(missingManifestClip, true);
            useVm.Reload();
            var useClipVm = useVm.Items.First(i => i.Id == useId);
            Assert(useClipVm.IsReferenceOnlyFile,
                "缺失 manifest 的仅引用项 VM 应标记 IsReferenceOnlyFile=true");
            bool usedEventFired = false;
            bool refUnavailEventFired = false;
            useVm.Used += _ => usedEventFired = true;
            useVm.FileReferenceUnavailable += () => refUnavailEventFired = true;
            useVm.Use(useClipVm);
            Assert(!usedEventFired,
                "MainViewModel.Use 对失效仅引用项不应触发 Used 事件");
            Assert(refUnavailEventFired,
                "MainViewModel.Use 对失效仅引用项应触发 FileReferenceUnavailable");
            Assert(useClipVm.IsReferenceInvalid,
                "MainViewModel.Use 后 VM IsReferenceInvalid 应为 true");

            // 恢复点用：有效仅引用项
            long recoverId = useClips.Add(singleClip, true);
            useVm.Reload();
            var recoverVm = useVm.Items.First(i => i.Id == recoverId);
            DateTime createdAtBeforeUse = useClips.GetById(recoverId)!.CreatedAt;
            Assert(!recoverVm.IsReferenceInvalid,
                $"恢复用 VM 初始 IsReferenceInvalid 应为 false（实际={recoverVm.IsReferenceInvalid}）");
            usedEventFired = false;
            useVm.Use(recoverVm);
            Assert(usedEventFired,
                "MainViewModel.Use 对有效仅引用项应触发 Used 事件");
            Assert(useClips.GetById(recoverId)!.CreatedAt == createdAtBeforeUse,
                "主面板卡片重用不得修改 CreatedAt 或把卡片拉到最前");

            // 10. ClipDragDataBuilder：失效项 referenceUnavailable=true, data=null；恢复后构建完整 FileDrop
            bool dragBuilt = ClipDragDataBuilder.TryBuild(missingManifestClip, out DataObject? dragData, out bool dragRefUnavail);
            Assert(!dragBuilt && dragRefUnavail && dragData is null,
                "ClipDragDataBuilder 对失效仅引用项：应返回 false + referenceUnavailable=true + data=null");
            bool dragRecovered = ClipDragDataBuilder.TryBuild(singleClip, out DataObject? recoveredData, out bool recoveredRefUnavail);
            Assert(dragRecovered && !recoveredRefUnavail && recoveredData is not null,
                "ClipDragDataBuilder 对恢复的仅引用项：应返回 true + referenceUnavailable=false");
            Assert(recoveredData!.GetDataPresent(DataFormats.FileDrop, autoConvert: false),
                "恢复后拖出数据应包含 FileDrop");

            // 11. 回归：空 RefPath + 旧"仅引用"标记 → fail-closed（不能按非引用继续触发 Used/自动粘贴）
            var oldMarkerClip = new ClipItem { Type = ClipType.File, PreviewText = "old-file（仅引用）", RefPath = null };
            ClipFileReferenceValidation oldMarkerResult = ClipFileReferenceValidator.Validate(oldMarkerClip);
            Assert(oldMarkerResult.IsReferenceOnly,
                "空 RefPath + 旧仅引用标记：应报告 IsReferenceOnly=true");
            Assert(!oldMarkerResult.IsValid,
                "空 RefPath + 旧仅引用标记：应 fail-closed 为 IsValid=false");
            Assert(oldMarkerResult.Paths.Count == 0,
                "空 RefPath + 旧仅引用标记：Paths 应为空");

            // 12. 回归：有效仅引用写回不向上抛异常（剪贴板保护）
            ClipWriteResult writeResult = refWriterValid.Write(singleClip);
            Assert(writeResult == ClipWriteResult.Completed,
                "有效仅引用写回应返回 Completed（异常保护内不抛）");

            // —— ByteSizeFormatter ——
            Assert(ByteSizeFormatter.Format(1023) == "1023 B",
                "ByteSizeFormatter < 1KB 应显示 B");
            Assert(ByteSizeFormatter.Format(1024) == "1.0 KB",
                "ByteSizeFormatter = 1KB 应显示 1.0 KB");
            Assert(ByteSizeFormatter.Format(1572864) == "1.5 MB",
                "ByteSizeFormatter = 1.5MB 应显示 1.5 MB");
            Assert(ByteSizeFormatter.Format(26214400) == "25.0 MB",
                "ByteSizeFormatter = 25MB 应显示 25.0 MB");
            Assert(ByteSizeFormatter.Format(1073741824) == "1.0 GB",
                "ByteSizeFormatter = 1GB 应显示 1.0 GB");
            Assert(ByteSizeFormatter.Format(0) == "0 B",
                "ByteSizeFormatter: 0 字节应显示 0 B");
            Assert(ByteSizeFormatter.Format(-1024) == "-1024 B",
                "ByteSizeFormatter: 负数不抛异常，按值显示 B");
            Assert(ByteSizeFormatter.Format(1099511627776) == "1024.0 GB",
                "ByteSizeFormatter: 1TB 应正确转为 1024.0 GB（不缺 TB 常量即不崩）");

            // —— TimeFormat 时间显示 ——
            RunTimeFormatTests();

            // —— ClipboardInternalWriteMarker 标记逻辑 ——
            RunClipboardInternalWriteMarkerTests();

            // —— PurgeExpired 返回被删项 + 跳过规则 ——
            var purgePaths = new AppPaths(Path.Combine(dir, "purge-expired"));
            var purgeDb = new Database(purgePaths);
            var purgeStore = new SqliteClipStore(purgeDb);
            DateTime now = DateTime.UtcNow;
            long pid1 = purgeStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "old-active", ContentHash = "p1", CreatedAt = now.AddDays(-10), IsPinned = false }, true);
            long pid2 = purgeStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "old-pinned", ContentHash = "p2", CreatedAt = now.AddDays(-10), IsPinned = true }, true);
            long pid3 = purgeStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "recent-active", ContentHash = "p3", CreatedAt = now, IsPinned = false }, true);
            long pid4 = purgeStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "old-deleted", ContentHash = "p4", CreatedAt = now.AddDays(-10), IsPinned = false }, true);
            // Add 硬编码 IsDeleted=0，手动置为回收站
            purgeStore.SoftDelete(pid4);
            var purgedExpired = purgeStore.PurgeExpired(3);
            Assert(purgedExpired.Count == 1 && purgedExpired[0].Id == pid1,
                "PurgeExpired 应只返回旧活动非置顶项");
            Assert(purgeStore.GetById(pid2) is not null && purgeStore.GetById(pid3) is not null && purgeStore.GetById(pid4) is not null,
                "PurgeExpired 应保留置顶/近期/回收站项");
            Assert(purgeStore.PurgeExpired(0).Count == 0,
                "PurgeExpired(0) 应返回空");

            // —— PurgeRecycleBin 返回被删项 ——
            var recyclePaths = new AppPaths(Path.Combine(dir, "purge-recycle-bin"));
            var rbStore = new SqliteClipStore(new Database(recyclePaths));
            long rid1 = rbStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "old-recycle", ContentHash = "r1", CreatedAt = now.AddDays(-10) }, true);
            long rid2 = rbStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "recent-recycle", ContentHash = "r2", CreatedAt = now }, true);
            rbStore.SoftDelete(rid1);
            rbStore.SoftDelete(rid2);
            // SoftDelete 设 DeletedAt=now，需要把 rid1 改回 10 天前才能过期
            using (var fixDb = new Database(recyclePaths).Open())
            {
                using var fixCmd = fixDb.CreateCommand();
                fixCmd.CommandText = "UPDATE clip_items SET DeletedAt = $d WHERE Id = $id;";
                fixCmd.Parameters.AddWithValue("$d", DateTime.UtcNow.AddDays(-10).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                fixCmd.Parameters.AddWithValue("$id", rid1);
                fixCmd.ExecuteNonQuery();
            }
            long rid3 = rbStore.Add(new ClipItem { Type = ClipType.Text, PreviewText = "active", ContentHash = "r3", CreatedAt = now.AddDays(-10), IsDeleted = false }, true);
            var purgedBin = rbStore.PurgeRecycleBin(3);
            Assert(purgedBin.Count == 1 && purgedBin[0].Id == rid1,
                "PurgeRecycleBin 应只返回旧回收站项");
            Assert(rbStore.GetById(rid2) is not null && rbStore.GetById(rid3) is not null,
                "PurgeRecycleBin 应保留近期回收站与活动项");

            // —— ClipItemFileEraser ——
            var erasePaths = new AppPaths(dir);
            var eraser = new ClipItemFileEraser(erasePaths);

            // Image: 图 + 缩略图
            string imgFile = Path.Combine(erasePaths.ImagesDir, "erase-test.png");
            string thumbFile = Path.Combine(erasePaths.ThumbsDir, "erase-test-thumb.png");
            File.WriteAllText(imgFile, "image");
            File.WriteAllText(thumbFile, "thumb");
            eraser.Erase(new ClipItem { Type = ClipType.Image, RefPath = imgFile, ThumbnailPath = thumbFile });
            Assert(!File.Exists(imgFile) && !File.Exists(thumbFile),
                "ClipItemFileEraser 应删除 Image 的原图与缩略图");

            // File（复制副本）：payload + manifest
            string erasePayloadDir = Path.Combine(erasePaths.FilePayloadsDir, "erase-payload");
            Directory.CreateDirectory(erasePayloadDir);
            string erasePayloadFile = Path.Combine(erasePayloadDir, "copy.txt");
            File.WriteAllText(erasePayloadFile, "copy");
            string eraseManifestPath = Path.Combine(erasePaths.FileManifestsDir, "erase-copy.clipora-files.json");
            var copyManifest = new ClipFileManifest
            {
                IsReferenceOnly = false,
                Entries =
                {
                    new ClipFileManifestEntry
                    {
                        OriginalPath = Path.Combine(Path.GetTempPath(), "user-original.txt"),
                        StoredPath = erasePayloadFile,
                        DisplayName = "copy.txt",
                    },
                },
            };
            copyManifest.Save(eraseManifestPath);
            // 在 Root 之外建一个"用户原文件"
            string userFile = Path.Combine(Path.GetTempPath(), "clipora-erase-user-original.txt");
            File.WriteAllText(userFile, "user original");
            eraser.Erase(new ClipItem { Type = ClipType.File, RefPath = eraseManifestPath });
            Assert(!File.Exists(eraseManifestPath), "File 副本 Erase 应删除 manifest");
            Assert(!File.Exists(erasePayloadFile) && !Directory.Exists(erasePayloadDir),
                "File 副本 Erase 应删除 payload 文件与子目录");
            Assert(File.Exists(userFile),
                "File 副本 Erase 绝不能删 Root 之外的用户原文件");

            // File（仅引用）：IsReferenceOnly=true，只删 manifest
            string refManifestPath = Path.Combine(erasePaths.FileManifestsDir, "erase-ref.clipora-files.json");
            string refUserFile = Path.Combine(Path.GetTempPath(), "clipora-erase-ref-original.txt");
            File.WriteAllText(refUserFile, "ref original");
            new ClipFileManifest
            {
                IsReferenceOnly = true,
                Entries =
                {
                    new ClipFileManifestEntry
                    {
                        OriginalPath = refUserFile,
                        StoredPath = null,
                        DisplayName = "ref.txt",
                    },
                },
            }.Save(refManifestPath);
            eraser.Erase(new ClipItem { Type = ClipType.File, RefPath = refManifestPath });
            Assert(!File.Exists(refManifestPath),
                "仅引用 File Erase 应删除 manifest");
            Assert(File.Exists(refUserFile),
                "仅引用 File Erase 绝不能删用户原文件");

            // 病态清单：StoredPath 错填成 OriginalPath（在 Root 之外）→ 绝不能删
            string sickManifestPath = Path.Combine(erasePaths.FileManifestsDir, "erase-sick.clipora-files.json");
            string sickUserFile = Path.Combine(Path.GetTempPath(), "clipora-erase-sick-original.txt");
            File.WriteAllText(sickUserFile, "sick");
            new ClipFileManifest
            {
                IsReferenceOnly = false,
                Entries =
                {
                    new ClipFileManifestEntry
                    {
                        OriginalPath = sickUserFile,
                        StoredPath = sickUserFile, // 病态：与 OriginalPath 相同，且在 Root 之外
                        DisplayName = "sick.txt",
                    },
                },
            }.Save(sickManifestPath);
            eraser.Erase(new ClipItem { Type = ClipType.File, RefPath = sickManifestPath });
            Assert(File.Exists(sickUserFile),
                "病态清单（StoredPath=OriginalPath 且在 Root 外）绝不能删用户原文件");
            // manifest 应被删（它在 Root 之下）
            Assert(!File.Exists(sickManifestPath),
                "病态清单 manifest 仍应删除");

            // 畸形 StoredPath（运行时构造含 null 字符，Path.GetFullPath 会抛）：
            // Erase 必须不抛，跳过该项并仍删 manifest（验证路径校验助手的防御 try/catch）。
            string malformedManifestPath = Path.Combine(erasePaths.FileManifestsDir, "erase-malformed.clipora-files.json");
            new ClipFileManifest
            {
                IsReferenceOnly = false,
                Entries =
                {
                    new ClipFileManifestEntry
                    {
                        OriginalPath = "orig",
                        StoredPath = "bad" + (char)0 + "path",
                        DisplayName = "bad",
                    },
                },
            }.Save(malformedManifestPath);
            eraser.Erase(new ClipItem { Type = ClipType.File, RefPath = malformedManifestPath });
            Assert(!File.Exists(malformedManifestPath),
                "畸形 StoredPath 不应让 Erase 抛异常，应跳过该项并仍删除 manifest");

            // Text：无文件项，Erase 不抛
            eraser.Erase(new ClipItem { Type = ClipType.Text, PreviewText = "no files" });
            // 不抛即通过

            // —— AppPaths 数据隔离解析（M4.2.1a + M4.2.2d） ——
            string envKey = "CLIPORA_DATA_DIR";
            string? savedEnv = Environment.GetEnvironmentVariable(envKey);
            // 全部使用注入 MemoryStorageRootLocator.None，不读真实 HKCU locator
            var isolationSvc = new StorageLocationService(MemoryStorageRootLocator.None);
            try
            {
                // 1. 构造参数覆盖环境变量
                string overridePath = Path.Combine(dir, "override-data");
                Environment.SetEnvironmentVariable(envKey, Path.Combine(dir, "should-be-ignored"));
                var pathsOverride = new AppPaths(overridePath, isolationSvc);
                Assert(pathsOverride.Root == Path.GetFullPath(overridePath),
                    "构造参数应覆盖环境变量");

                // 2. 环境变量 → Root
                string envPath = Path.Combine(dir, "env-data");
                Environment.SetEnvironmentVariable(envKey, envPath);
                var pathsEnv = new AppPaths(null, isolationSvc);
                Assert(pathsEnv.Root == Path.GetFullPath(envPath),
                    "无构造参数时应使用 CLIPORA_DATA_DIR 环境变量");

                // 3. 环境变量为空 + 无 locator → 默认 %LOCALAPPDATA%\Clipora
                Environment.SetEnvironmentVariable(envKey, null);
                var resolutionDefault = isolationSvc.Resolve();
                string expectedDefault = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Clipora");
                Assert(string.Equals(resolutionDefault.Root, expectedDefault, StringComparison.OrdinalIgnoreCase),
                    $"无构造参数、无环境变量、无 locator 时应为默认正式目录（期望={expectedDefault}，实际={resolutionDefault.Root}）");
                Assert(resolutionDefault.Source == StorageRootSource.Default,
                    "无任何输入时应为 Default 来源");
                Assert(resolutionDefault.CanMigrate,
                    "默认目录 CanMigrate 应为 true");

                // 4. 构造参数相对路径 → 抛 StorageLocationException
                bool threwOnRelative = false;
                try { new AppPaths("relative/path", isolationSvc); }
                catch (StorageLocationException ex)
                {
                    threwOnRelative = true;
                    Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                        "相对构造参数错误码应为 InvalidPath");
                }
                Assert(threwOnRelative,
                    "构造参数为相对路径应抛 StorageLocationException");

                // 4b. C:relative（IsPathRooted 会接受但 IsPathFullyQualified 拒绝）
                bool threwCRelative = false;
                try { new AppPaths("C:relative", isolationSvc); }
                catch (StorageLocationException ex)
                {
                    threwCRelative = true;
                    Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                        "C:relative 错误码应为 InvalidPath（IsPathFullyQualified 拒绝）");
                }
                Assert(threwCRelative,
                    "C:relative 应被 IsPathFullyQualified 拒绝");

                // 5. 环境变量为相对路径 → 抛 StorageLocationException
                Environment.SetEnvironmentVariable(envKey, "relative/env");
                bool threwOnRelativeEnv = false;
                try { new AppPaths(null, isolationSvc); }
                catch (StorageLocationException ex)
                {
                    threwOnRelativeEnv = true;
                    Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                        "相对环境变量错误码应为 InvalidPath");
                }
                Assert(threwOnRelativeEnv,
                    "环境变量为相对路径应抛 StorageLocationException");
            }
            finally
            {
                // 恢复调用进程的环境变量
                if (savedEnv is null)
                    Environment.SetEnvironmentVariable(envKey, null);
                else
                    Environment.SetEnvironmentVariable(envKey, savedEnv);
            }

            // —— StorageLocationService 优先级与 fail-closed（M4.2.2d） ——
            RunStorageLocationTests(dir);

            // —— StorageMigrationEngine 迁移引擎（M4.2.2e） ——
            RunStorageMigrationTests();

            // —— RegistryStorageMigrationStateStore（M4.2.3a-1） ——
            var registryTestTracker = new SelfTestDirectoryTracker();
            RunRegistryStateStoreTests(registryTestTracker);

            // —— StorageMigrationStartupCoordinator（M4.2.3a-2） ——
            var coordinatorTracker = new SelfTestDirectoryTracker();
            RunStartupCoordinatorTests(coordinatorTracker, registryTestTracker);

            // —— StorageMigrationRestartCoordinator（M4.2.3a-2c） ——
            RunStorageMigrationRestartTests();

            // —— StorageMigrationPlanService（M4.2.3a-3a） ——
            RunStorageMigrationPlanTests();

            // —— StorageMigrationSettingsViewModel（M4.2.3a-3b） ——
            RunStorageMigrationSettingsTests();

            // —— StorageMigrationProgressPresenter（M4.2.3a-3c-1） ——
            RunStorageMigrationProgressPresenterTests();

            // —— StorageStartupWindowPolicy（M4.2.3a-3c-2） ——
            RunStorageStartupWindowPolicyTests();

            // —— StorageDefaultRecoveryService（M4.2.3a-3c-1） ——
            RunStorageDefaultRecoveryTests();

            // —— 外部打开策略：危险类型必须确认，测试使用 fake launcher ——
            RunExternalOpenPolicyTests();

            // —— Release/Debug 崩溃诊断脱敏与清理 ——
            RunCrashDiagnosticTests(dir);

            // —— 隐私：PrivacyCapturePolicy 纯函数（M4.4.2a） ——
            RunPrivacyCapturePolicyTests();

            // —— 隐私：ClipboardExclusionMarker 纯函数（4.4.2b 预留，本步跳过活体剪贴板） ——
            RunClipboardExclusionMarkerTests();

            // —— 隐私：RunningAppsProvider 核心纯函数（M4.4.2a） ——
            RunRunningAppsProviderTests();

            // —— 隐私：SettingsViewModel 排除名单管理（M4.4.2a） ——
            RunPrivacySettingsViewModelTests();

            // —— 快捷键：HotkeyGesture 解析/格式化（M4.5.2a） ——
            RunHotkeyGestureTests();

            // —— 快捷键：HotkeyConflictChecker 重复检测（M4.5.2a） ——
            RunHotkeyConflictCheckerTests();

            // —— 顺序粘贴：BurstPlanner 批次计算（M4.5-seq） ——
            RunSequentialPasteBurstPlannerTests();

            // —— 顺序粘贴：SequentialPasteSession 状态机（M4.5-seq） ——
            RunSequentialPasteSessionTests();

            // —— 顺序粘贴：存储层必须忽略置顶，直接按时间截断 ——
            RunSequentialPasteStoreQueryTest(dir);

            // —— 快捷粘贴：必须等待原热键主键与修饰键全部释放 ——
            RunHotkeyPasteReleaseGateTests();

            // —— 托盘启动策略：CreateTray 失败必须强制显示主面板 ——
            RunTrayStartupPolicyTests();

            // —— OCR 存储层纯函数 ——
            RunOcrStoreTests(clips, settingsStub);

            // —— 备份导入导出纯函数 ——
            RunBackupTests(dir, db, clips, settings);

            // —— 数据库损坏自动恢复 ——
            RunDatabaseRecoveryTests(dir);

            // —— 主题服务烟雾测试（M6.1b） ——
            RunThemeServiceTests();

            // —— 图片缩略图：回收容器改绑图片 VM 后仍须触发惰性加载（M6.3 bugfix） ——
            RunImageThumbnailContainerTests(paths, settings);

            // —— 备份导入恢复 RecoverAll 测试（M6.1b） ——
            RunBackupRecoveryTests(dir, db);

            // —— 平滑滚动重构（行为保持）：缓动/滚动编排/回到顶部滞回 ——
            RunScrollingTests();

            // —— CliporaCard 固定命中层：视觉上浮后原始底边仍可命中 ——
            RunCliporaCardHitTest();

            // —— 图片大预览：悬停状态、滚动隔离、稳定定位与有界 STA 解码 ——
            RunImagePreviewTests(dir);

            // —— 时间段分组策略重构（行为保持）：段键/默认展开/捕获展开/置顶/点击翻转 ——
            RunGroupingTests();

            // —— 标签管理编排重构（行为保持）：改名去重/上下移重排序/改色无变化判定 ——
            RunTagManagementTests();

            // —— 文件名省略（行为保持）：验证文本修剪后不溢出可用宽度 ——
            RunFileNameTrimmingTests();

            // 清理临时目录（068 N1 — 集中清理，防止 %TEMP% 泄漏）
            registryTestTracker.CleanAll();
            coordinatorTracker.CleanAll();

            // —— 自检残留核对：验证 tracker 已清空 ——
            Assert(registryTestTracker.RemainingCount == 0,
                $"注册表自检残留目录应为 0（实际={registryTestTracker.RemainingCount}）");
            Assert(coordinatorTracker.RemainingCount == 0,
                $"协调器自检临时目录残留应为 0（实际={coordinatorTracker.RemainingCount}）");

            File.WriteAllText(resultPath, "SELFTEST OK");
            Console.WriteLine("SELFTEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "SELFTEST FAIL: " + ex);
            Console.WriteLine("SELFTEST FAIL: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void RunImageThumbnailContainerTests(AppPaths paths, ISettingsService settings)
    {
        using var imagePreviewLoader = new ImagePreviewLoader();
        string thumbnailPath = Path.Combine(paths.ThumbsDir, "recycled-thumbnail.png");
        BitmapSource bitmap = BitmapSource.Create(
            2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[]
            {
                0, 0, 255, 255, 0, 255, 0, 255,
                255, 0, 0, 255, 255, 255, 255, 255,
            },
            8);
        ClipboardImageNormalizer.SavePng(bitmap, thumbnailPath);
        string capturedThumbnailPath = Path.Combine(paths.ThumbsDir, "captured-thumbnail.png");
        ClipboardImageNormalizer.SavePng(bitmap, capturedThumbnailPath);

        var textVm = new ClipItemViewModel(new ClipItem
        {
            Type = ClipType.Text,
            PreviewText = "recycled text",
            CreatedAt = DateTime.UtcNow,
        });
        var imageVm = new ClipItemViewModel(new ClipItem
        {
            Type = ClipType.Image,
            PreviewText = "recycled image",
            ThumbnailPath = thumbnailPath,
            CreatedAt = DateTime.UtcNow,
        });
        var items = new ObservableCollection<ClipItemViewModel> { textVm };
        var window = new HistoryWindow(settings, imagePreviewLoader)
        {
            AllowClose = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Opacity = 0,
        };

        try
        {
            window.ItemsList.ItemsSource = items;
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(140));

            items[0] = imageVm;
            PumpDispatcher(TimeSpan.FromMilliseconds(300));

            Assert(imageVm.Thumbnail is not null,
                "回收容器改绑图片 VM 后应触发缩略图加载");
        }
        finally
        {
            window.Close();
        }

        // 精确覆盖生产链路：ClipCaptured → MainViewModel.AddOrBump → Items.Add →
        // ICollectionView 按时间移到顶部 → 虚拟化容器呈现新图片卡片。
        var capturePaths = new AppPaths(Path.Combine(paths.Root, "thumbnail-capture-test"));
        var captureDatabase = new Database(capturePaths);
        var captureClips = new SqliteClipStore(captureDatabase);
        var captureTags = new SqliteTagStore(captureDatabase);
        DateTime captureAt = DateTime.UtcNow;
        for (int i = 0; i < 20; i++)
        {
            captureClips.Add(new ClipItem
            {
                Type = ClipType.Text,
                PreviewText = $"capture baseline {i}",
                TextContent = $"capture baseline {i}",
                ContentHash = "capture-baseline-" + Guid.NewGuid().ToString("N"),
                SizeBytes = 20,
                CreatedAt = captureAt.AddSeconds(-i - 1),
            }, mergeDuplicates: true);
        }

        var monitor = new SelfTestMonitor();
        var mainVm = new MainViewModel(captureClips, captureTags, monitor, new ClipWriter());
        var captureWindow = new HistoryWindow(settings, imagePreviewLoader)
        {
            DataContext = mainVm,
            AllowClose = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Opacity = 0.01,
        };

        try
        {
            captureWindow.Show();
            captureWindow.UpdateLayout();
            captureWindow.ItemsList.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(180));

            var captured = new ClipItem
            {
                Type = ClipType.Image,
                PreviewText = "new captured image",
                RefPath = capturedThumbnailPath,
                ThumbnailPath = capturedThumbnailPath,
                ContentHash = "new-captured-image-" + Guid.NewGuid().ToString("N"),
                SizeBytes = new FileInfo(capturedThumbnailPath).Length,
                CreatedAt = captureAt.AddSeconds(1),
            };
            captured.Id = captureClips.Add(captured, mergeDuplicates: true);
            monitor.RaiseClipCaptured(captured);
            ClipItemViewModel capturedVm = mainVm.Items.Single(vm => vm.Id == captured.Id);
            captureWindow.ItemsList.ScrollIntoView(capturedVm);
            captureWindow.UpdateLayout();
            captureWindow.ItemsList.UpdateLayout();
            for (int i = 0; i < 40 && capturedVm.Thumbnail is null; i++)
                PumpDispatcher(TimeSpan.FromMilliseconds(50));

            int viewIndex = mainVm.ItemsView.Cast<object>().ToList().IndexOf(capturedVm);
            Assert(capturedVm.Thumbnail is not null,
                $"ClipCaptured 新增到列表顶部的图片卡片应触发缩略图加载"
                + $"（viewIndex={viewIndex}, fileExists={File.Exists(capturedVm.Model.ThumbnailPath)}）");
        }
        finally
        {
            captureWindow.Close();
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>清理超过 24 小时的 %TEMP% 自检残留，防止无限堆积。</summary>
    private static void CleanLegacyTempFiles()
    {
        string tempPath = Path.GetTempPath();
        DateTime cutoff = DateTime.UtcNow.AddHours(-24);

        // clipora-selftest-<guid> 目录
        foreach (string legacyDir in Directory.GetDirectories(tempPath, "clipora-selftest-*"))
        {
            try { if (Directory.GetLastWriteTimeUtc(legacyDir) < cutoff) Directory.Delete(legacyDir, true); }
            catch { /* best-effort */ }
        }

        // clipora-drag-selftest-<guid> 目录
        foreach (string legacyDir in Directory.GetDirectories(tempPath, "clipora-drag-selftest-*"))
        {
            try { if (Directory.GetLastWriteTimeUtc(legacyDir) < cutoff) Directory.Delete(legacyDir, true); }
            catch { /* best-effort */ }
        }

        // Clipora_SelfTest_* 迁移测试残留
        foreach (string legacyDir in Directory.GetDirectories(tempPath, "Clipora_SelfTest_*"))
        {
            try { if (Directory.GetLastWriteTimeUtc(legacyDir) < cutoff) Directory.Delete(legacyDir, true); }
            catch { /* best-effort */ }
        }

        // 孤立结果文件
        string[] legacyFiles = new[]
        {
            "clipora-selftest-result.txt",
            "clipora-drag-selftest-result.txt",
            "clipora-dump.txt",
            "clipora-ocr-status.txt",
        };

        foreach (string fileName in legacyFiles)
        {
            string path = Path.Combine(tempPath, fileName);
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch { /* best-effort */ }
        }
    }

    private static void RunStorageLocationTests(string dir)
    {
        // —— 优先级链：override > env > locator > default ——
        string overridePath = Path.Combine(dir, "override-data");
        Directory.CreateDirectory(overridePath);
        string envPath = Path.Combine(dir, "env-data");
        Directory.CreateDirectory(envPath);
        string locatorPath = Path.Combine(dir, "locator-data");
        Directory.CreateDirectory(locatorPath);

        // 1. override 最高优先级，CanMigrate=false
        var svcEnv = new StorageLocationService(new MemoryStorageRootLocator(locatorPath));
        Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", envPath);
        try
        {
            var r1 = svcEnv.Resolve(overridePath);
            Assert(r1.Root == Path.GetFullPath(overridePath),
                "override 应优先于环境变量");
            Assert(r1.Source == StorageRootSource.Override,
                "override 来源应为 Override");
            Assert(!r1.CanMigrate,
                "override 时 CanMigrate 应为 false");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", null);
        }

        // 2a. override 含 NUL → IsPathFullyQualified/GetFullPath 裸异常被外层兜底包裹
        bool threwOverrideInvalid = false;
        try { svcEnv.Resolve("C:\\bad" + (char)0 + "path"); }
        catch (StorageLocationException ex)
        {
            threwOverrideInvalid = true;
            Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                $"override 含 NUL 字符异常应包裹为 InvalidPath（实际 {ex.ErrorCode}）");
        }
        Assert(threwOverrideInvalid,
            "override 路径异常应转换为 StorageLocationException（不得裸抛 ArgumentException/IOException）");

        // 2. override 为相对路径 → fail-closed
        bool threwOverrideRelative = false;
        try { svcEnv.Resolve("relative/override"); }
        catch (StorageLocationException ex)
        {
            threwOverrideRelative = true;
            Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                "相对 override 错误码应为 InvalidPath");
        }
        Assert(threwOverrideRelative,
            "相对 override 应抛 StorageLocationException");

        // 3. 环境变量优先级（无 override），CanMigrate=false
        Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", envPath);
        try
        {
            var r3 = svcEnv.Resolve();
            Assert(r3.Root == Path.GetFullPath(envPath),
                "环境变量应优先于 locator");
            Assert(r3.Source == StorageRootSource.Environment,
                "环境变量来源应为 Environment");
            Assert(!r3.CanMigrate,
                "环境变量时 CanMigrate 应为 false");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", null);
        }

        // 4. 环境变量为相对路径 → fail-closed
        Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", "relative/env");
        try
        {
            bool threwEnvRelative = false;
            try { svcEnv.Resolve(); }
            catch (StorageLocationException ex)
            {
                threwEnvRelative = true;
                Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                    "相对环境变量错误码应为 InvalidPath");
            }
            Assert(threwEnvRelative,
                "相对环境变量应抛 StorageLocationException");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", null);
        }

        // 5. locator 合法绝对现存目录，CanMigrate=true
        var svcLocator = new StorageLocationService(new MemoryStorageRootLocator(locatorPath));
        var r5 = svcLocator.Resolve();
        Assert(r5.Root == Path.GetFullPath(locatorPath),
            "locator 合法绝对现存目录应解析为 Root");
        Assert(r5.Source == StorageRootSource.Locator,
            "locator 来源应为 Locator");
        Assert(r5.CanMigrate,
            "locator 时 CanMigrate 应为 true");

        // 6. locator 缺失 → 回退默认，CanMigrate=true
        var svcNoLocator = new StorageLocationService(MemoryStorageRootLocator.None);
        var r6 = svcNoLocator.Resolve();
        string expectedDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipora");
        Assert(string.Equals(r6.Root, expectedDefault, StringComparison.OrdinalIgnoreCase),
            $"locator 缺失应回退默认目录（期望={expectedDefault}，实际={r6.Root}）");
        Assert(r6.Source == StorageRootSource.Default,
            "locator 缺失来源应为 Default");
        Assert(r6.CanMigrate,
            "默认目录 CanMigrate 应为 true");

        // 7. locator 相对路径 → fail-closed (InvalidPath)
        var svcRelLocator = new StorageLocationService(new MemoryStorageRootLocator("relative/locator"));
        bool threwRelLocator = false;
        try { svcRelLocator.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwRelLocator = true;
            Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                "相对 locator 错误码应为 InvalidPath");
        }
        Assert(threwRelLocator,
            "相对 locator 应抛 StorageLocationException（fail-closed）");

        // 8. locator 目录不存在 → fail-closed (MissingDirectory)
        string missingPath = Path.Combine(dir, "does-not-exist");
        var svcMissing = new StorageLocationService(new MemoryStorageRootLocator(missingPath));
        bool threwMissing = false;
        try { svcMissing.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwMissing = true;
            Assert(ex.ErrorCode == StorageLocationError.MissingDirectory,
                $"目录不存在错误码应为 MissingDirectory（实际={ex.ErrorCode}）");
            Assert(string.Equals(ex.PathValue, Path.GetFullPath(missingPath), StringComparison.OrdinalIgnoreCase),
                "MissingDirectory 应携带结构化规范路径，UI 不得解析 Message");
        }
        Assert(threwMissing,
            "locator 目标不存在应抛 StorageLocationException（fail-closed，不得创建空目录）");

        // 9. locator UNC 路径 → fail-closed (UnsupportedNetworkPath)
        var svcUnc = new StorageLocationService(new MemoryStorageRootLocator(@"\\server\share\path"));
        bool threwUnc = false;
        try { svcUnc.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwUnc = true;
            Assert(ex.ErrorCode == StorageLocationError.UnsupportedNetworkPath,
                "UNC 路径错误码应为 UnsupportedNetworkPath");
        }
        Assert(threwUnc,
            "UNC locator 应抛 StorageLocationException（fail-closed）");

        // 9b. \\?\ 本地扩展路径不被误判为网络路径
        string extendedLocal = @"\\?\C:\extended\path";
        var svcExtendedLocal = new StorageLocationService(new MemoryStorageRootLocator(extendedLocal));
        // ValidateLocatorRoot 在校验阶段因目录不存在而抛 MissingDirectory（而非 UnsupportedNetworkPath）
        bool threwExtendedLocal = false;
        try { svcExtendedLocal.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwExtendedLocal = true;
            // \\?\C:\... 是合法的本地扩展路径，应跳过 UNC 分支进入目录存在性检查
            Assert(ex.ErrorCode == StorageLocationError.MissingDirectory,
                $@"\\?\ 本地扩展路径不应被误判为网络路径（期望 MissingDirectory，实际 {ex.ErrorCode}）");
        }
        Assert(threwExtendedLocal,
            @"\\?\ 本地扩展路径应到达目录存在性检查而非 UnsupportedNetworkPath");

        // 9c. \\?\UNC\server\share 扩展网络路径 → fail-closed
        var svcExtendedUnc = new StorageLocationService(new MemoryStorageRootLocator(@"\\?\UNC\server\share"));
        bool threwExtendedUnc = false;
        try { svcExtendedUnc.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwExtendedUnc = true;
            Assert(ex.ErrorCode == StorageLocationError.UnsupportedNetworkPath,
                $@"\\?\UNC 网络路径错误码应为 UnsupportedNetworkPath（实际={ex.ErrorCode}）");
        }
        Assert(threwExtendedUnc,
            @"\\?\UNC 网络路径应被拒绝（fail-closed）");

        // 10. 异常后环境变量无残留
        string? envAfter = Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
        Assert(envAfter is null,
            $"异常后环境变量不应残留（实际={envAfter ?? "(null)"}）");

        // 10b. 已解析 resolution 可直接构造 AppPaths，保持来源/迁移能力且不二次解析。
        var pathsFromResolution = new AppPaths(r5);
        Assert(pathsFromResolution.Root == r5.Root
            && pathsFromResolution.RootSource == StorageRootSource.Locator
            && pathsFromResolution.CanMigrate,
            "AppPaths(resolution) 应保留同一次受控解析结果");

        // 11. locator 包含非法字符 → fail-closed
        var svcBadChars = new StorageLocationService(new MemoryStorageRootLocator("C:\\bad" + (char)0 + "path"));
        bool threwBadChars = false;
        try { svcBadChars.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwBadChars = true;
            Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                "含 NUL 非法字符的 locator 错误码应为 InvalidPath");
        }
        Assert(threwBadChars,
            "非法字符 locator 应抛 StorageLocationException（fail-closed）");

        // 11b. locator 抛 StorageLocationException（模拟注册表类型错误）→ 传播
        var svcThrowing = new StorageLocationService(new ThrowingStorageRootLocator(
            new StorageLocationException(StorageLocationError.InvalidPath, "注册表值类型错误（期望 REG_SZ，实际 REG_DWORD）")));
        bool threwTypeErr = false;
        try { svcThrowing.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwTypeErr = true;
            Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                "注册表类型错误应传播 InvalidPath");
            Assert(ex.Message.Contains("REG_DWORD", StringComparison.Ordinal),
                "错误消息应包含实际类型");
        }
        Assert(threwTypeErr,
            "locator 抛 StorageLocationException 应传播到 Resolve（注册表类型错误不可静默回退默认库）");

        // 11b2. locator 抛 AccessDenied（模拟注册表权限异常）→ 传播
        var svcAccessDenied = new StorageLocationService(new ThrowingStorageRootLocator(
            new StorageLocationException(StorageLocationError.AccessDenied, "无法访问注册表 HKCU\\Software\\Clipora\\Storage: 拒绝访问")));
        bool threwAccessDenied = false;
        try { svcAccessDenied.Resolve(); }
        catch (StorageLocationException ex)
        {
            threwAccessDenied = true;
            Assert(ex.ErrorCode == StorageLocationError.AccessDenied,
                "注册表权限异常应传播 AccessDenied");
        }
        Assert(threwAccessDenied,
            "locator AccessDenied 应传播到 Resolve（不可静默回退）");

        // 11c. 本地磁盘路径不被误判为网络驱动器（DriveInfo 检测）
        string localDrivePath = Path.Combine(dir, "local-drive-test");
        Directory.CreateDirectory(localDrivePath);
        var svcLocalDrive = new StorageLocationService(new MemoryStorageRootLocator(localDrivePath));
        var rLocalDrive = svcLocalDrive.Resolve();
        Assert(rLocalDrive.Root == Path.GetFullPath(localDrivePath),
            "本地磁盘路径应正常解析");
        Assert(rLocalDrive.Source == StorageRootSource.Locator,
            "本地磁盘路径来源应为 Locator");

        // 12. AppPaths 集成：注入构造返回正确的 RootSource/CanMigrate
        var overrideInjected = new StorageLocationService(MemoryStorageRootLocator.None);
        var pathsOverride = new AppPaths(overridePath, overrideInjected);
        Assert(pathsOverride.Root == Path.GetFullPath(overridePath),
            "AppPaths 注入构造：override Root 应正确");
        Assert(pathsOverride.RootSource == StorageRootSource.Override,
            "AppPaths 注入构造：override RootSource 应为 Override");
        Assert(!pathsOverride.CanMigrate,
            "AppPaths 注入构造：override CanMigrate 应为 false");

        var pathsLocator = new AppPaths(null, svcLocator);
        Assert(pathsLocator.Root == Path.GetFullPath(locatorPath),
            "AppPaths 注入构造：locator Root 应正确");
        Assert(pathsLocator.RootSource == StorageRootSource.Locator,
            "AppPaths 注入构造：locator RootSource 应为 Locator");
        Assert(pathsLocator.CanMigrate,
            "AppPaths 注入构造：locator CanMigrate 应为 true");

        // 默认根改用纯解析校验：AppPaths 构造函数会 Directory.CreateDirectory(Root)，
        // 构造 Default 会污染真实 %LOCALAPPDATA%\Clipora（违反"自检不得创建/写正式目录"，见 052 §85）。
        // 属性转发逻辑（RootSource/CanMigrate ← Resolution）已由上面 Override/Locator 的真实构造覆盖。
        var resDefault = svcNoLocator.Resolve();
        Assert(string.Equals(resDefault.Root, expectedDefault, StringComparison.OrdinalIgnoreCase),
            "默认解析：Root 应为 %LOCALAPPDATA%\\Clipora");
        Assert(resDefault.Source == StorageRootSource.Default,
            "默认解析：RootSource 应为 Default");
        Assert(resDefault.CanMigrate,
            "默认解析：CanMigrate 应为 true");

        // 13. 回归：Override 后 env 无残留（覆盖 env 变量测试后确保清理）
        string? envFinal = Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
        Assert(envFinal is null,
            $"所有测试后 CLIPORA_DATA_DIR 应无残留（实际={envFinal ?? "(null)"}）");
    }

    private static void RunStorageMigrationTests()
    {
        var tracker = new SelfTestDirectoryTracker();
        try
        {
            // —— 成功基线：完整迁移 ——
            RunMigrationSuccessBaseline(tracker);

            // —— 二次迁移：成功 Root 的旧 marker 不得与新 staging marker 冲突 ——
            RunMigrationTwoHopMarkerRegression(tracker);

            // —— 校验拒绝 ——
            RunMigrationValidationRejections(tracker);

            // —— 故障点矩阵 ——
            RunMigrationFaultPointMatrix(tracker);

            // —— 恢复语义 ——
            RunMigrationRecoverySemantics(tracker);

            // —— 进度回调异常隔离 ——
            RunMigrationProgressIsolation(tracker);
        }
        finally
        {
            tracker.CleanAll();
        }
    }

    private static void RunMigrationSuccessBaseline(SelfTestDirectoryTracker tracker)
    {
        // 统一使用 %TEMP%\Clipora_SelfTest_*（§11 要求）
        string source = tracker.Track(CreateTestSource("mig-success-"));
        // 迁移前快照：source 业务行 + 附件 SHA-256（Observation-7）
        var preSnapshot = TakeSourceSnapshot(source);
        string target = tracker.Track(Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_success_" + Guid.NewGuid().ToString("N")));
        var migId = Guid.NewGuid();
        var stateStore = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, target, migId, null));
        var engine = new StorageMigrationEngine(stateStore);
        var result = engine.Execute(new StorageMigrationRequest(source, target, migId));
        Assert(result.Succeeded, $"成功基线应成功: {result.Detail}");
        Assert(result.Error == StorageMigrationError.None, "成功基线 Error 应为 None");
        Assert(result.TargetWasPromoted, "成功基线 TargetWasPromoted 应为 true");
        Assert(Directory.Exists(result.ActiveRoot), "ActiveRoot 应存在");
        Assert(File.Exists(Path.Combine(result.ActiveRoot, "clipora.db")), "target 应含 clipora.db");

        var finalState = stateStore.Read();
        Assert(string.Equals(Path.GetFullPath(finalState.ActiveRoot), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "state ActiveRoot 应指向 target");
        Assert(finalState.LastSourceRoot is not null, "state LastSourceRoot 应有值");
        Assert(finalState.PendingRoot is null, "state PendingRoot 应清除");
        Assert(finalState.MigrationId is null, "state MigrationId 应清除");
        Assert(Directory.Exists(source), "source 应保留");
        // Observation‑7: source 业务内容与附件 hash 不变
        var postSnapshot = TakeSourceSnapshot(source);
        Assert(preSnapshot.ClipCount == postSnapshot.ClipCount, "迁移前后 clip_items 行数一致");
        Assert(preSnapshot.AttachHash == postSnapshot.AttachHash, $"迁移前后附件 SHA‑256 一致（pre={preSnapshot.AttachHash}，post={postSnapshot.AttachHash}）");

        var marker = StorageMigrationMarker.Read(target);
        Assert(marker is not null && marker!.Phase == StorageMigrationPhase.Completed.ToString(),
            "target marker 应为 Completed");
    }

    private static void RunMigrationTwoHopMarkerRegression(SelfTestDirectoryTracker tracker)
    {
        string sourceA = tracker.Track(CreateTestSource("mig-two-hop-a-"));
        string nestedDirectory = Path.Combine(sourceA, "user-content");
        Directory.CreateDirectory(nestedDirectory);
        string nestedMarkerA = Path.Combine(nestedDirectory, ".clipora-migration.json");
        const string nestedMarkerContent = "ordinary user file with marker-like name";
        File.WriteAllText(nestedMarkerA, nestedMarkerContent, Encoding.UTF8);

        string targetB = tracker.Track(Path.Combine(
            Path.GetTempPath(),
            "Clipora_SelfTest_Target_two_hop_b_" + Guid.NewGuid().ToString("N")));
        Guid firstId = Guid.NewGuid();
        var firstStore = new MemoryStorageMigrationStateStore(
            new StorageMigrationState(sourceA, targetB, firstId, null));
        var firstResult = new StorageMigrationEngine(firstStore).Execute(
            new StorageMigrationRequest(sourceA, targetB, firstId));

        Assert(firstResult.Succeeded, $"两跳迁移 A→B 应成功: {firstResult.Detail}");
        StorageMigrationState firstState = firstStore.Read();
        Assert(string.Equals(firstState.ActiveRoot, targetB, StringComparison.OrdinalIgnoreCase)
            && firstState.PendingRoot is null
            && firstState.MigrationId is null,
            "两跳迁移 A→B 应提交 target 并清 pending/id");

        string rootMarkerB = StorageMigrationMarker.GetMarkerPath(targetB);
        Assert(File.Exists(rootMarkerB), "两跳迁移：B 应保留根 marker");
        string rootMarkerBBefore = File.ReadAllText(rootMarkerB, Encoding.UTF8);
        var markerB = StorageMigrationMarker.Read(targetB);
        Assert(markerB is not null
            && markerB.Phase == StorageMigrationPhase.Completed.ToString()
            && Guid.TryParse(markerB.MigrationId, out Guid markerBId)
            && markerBId == firstId,
            "两跳迁移：B 根 marker 应属于第一次迁移且为 Completed");
        Assert(File.ReadAllText(Path.Combine(targetB, "user-content", ".clipora-migration.json"), Encoding.UTF8)
            == nestedMarkerContent,
            "两跳迁移：B 应复制嵌套同名普通文件");

        string targetC = tracker.Track(Path.Combine(
            Path.GetTempPath(),
            "Clipora_SelfTest_Target_two_hop_c_" + Guid.NewGuid().ToString("N")));
        Guid secondId = Guid.NewGuid();
        var secondStore = new MemoryStorageMigrationStateStore(
            new StorageMigrationState(targetB, targetC, secondId, null));
        var secondResult = new StorageMigrationEngine(secondStore).Execute(
            new StorageMigrationRequest(targetB, targetC, secondId));

        Assert(secondResult.Succeeded, $"两跳迁移 B→C 应成功: {secondResult.Detail}");
        StorageMigrationState secondState = secondStore.Read();
        Assert(string.Equals(secondState.ActiveRoot, targetC, StringComparison.OrdinalIgnoreCase)
            && secondState.PendingRoot is null
            && secondState.MigrationId is null,
            "两跳迁移 B→C 应提交 target 并清 pending/id");
        Assert(Directory.Exists(sourceA) && Directory.Exists(targetB) && Directory.Exists(targetC),
            "两跳迁移成功后 A/B/C 均应保留");
        Assert(File.ReadAllText(rootMarkerB, Encoding.UTF8) == rootMarkerBBefore,
            "两跳迁移：第二次迁移不得修改 B 的根 marker");

        var markerC = StorageMigrationMarker.Read(targetC);
        Assert(markerC is not null
            && markerC.Phase == StorageMigrationPhase.Completed.ToString()
            && Guid.TryParse(markerC.MigrationId, out Guid markerCId)
            && markerCId == secondId,
            "两跳迁移：C 根 marker 应属于第二次迁移且为 Completed");
        Assert(File.ReadAllText(Path.Combine(targetC, "user-content", ".clipora-migration.json"), Encoding.UTF8)
            == nestedMarkerContent,
            "两跳迁移：C 应保留并验证嵌套同名普通文件");

        string stagingB = Path.Combine(
            Path.GetDirectoryName(targetB)!,
            "." + Path.GetFileName(targetB) + ".clipora-migrating-" + firstId.ToString("N"));
        string stagingC = Path.Combine(
            Path.GetDirectoryName(targetC)!,
            "." + Path.GetFileName(targetC) + ".clipora-migrating-" + secondId.ToString("N"));
        Assert(!Directory.Exists(stagingB) && !Directory.Exists(stagingC),
            "两跳迁移成功后 staging 均应不存在");
    }

    private static void RunMigrationValidationRejections(SelfTestDirectoryTracker tracker)
    {
        string source = tracker.Track(CreateTestSource("mig-val-"));
        string target = tracker.Track(Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_val_" + Guid.NewGuid().ToString("N")));
        var migId = Guid.NewGuid();

        // target 已存在 → 拒绝
        Directory.CreateDirectory(target);
        var stateStore1 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, target, migId, null));
        var engine1 = new StorageMigrationEngine(stateStore1);
        var r1 = engine1.Execute(new StorageMigrationRequest(source, target, Guid.NewGuid()));
        Assert(!r1.Succeeded && r1.Error == StorageMigrationError.StateMismatch,
            "target 已存在但无匹配 pending → StateMismatch");
        try { Directory.Delete(target, true); } catch { /* already deleted */ }

        // source 缺失 → 拒绝
        var stateStore2 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source + "_missing", target, migId, null));
        var engine2 = new StorageMigrationEngine(stateStore2);
        // Use the missing path as source
        var r2 = engine2.Execute(new StorageMigrationRequest(source + "_missing", target, Guid.NewGuid()));
        Assert(!r2.Succeeded, "source 缺失应失败");
        try { Directory.Delete(target, true); } catch { /* already deleted */ }

        // 同目录 → 拒绝
        var stateStore3 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, source, migId, null));
        var engine3 = new StorageMigrationEngine(stateStore3);
        var r3 = engine3.Execute(new StorageMigrationRequest(source, source, Guid.NewGuid()));
        Assert(!r3.Succeeded, "source==target 应拒绝");

        // 互为父子 → 拒绝（blank state，不用 pending 干扰）
        string childTarget = tracker.Track(Path.Combine(source, "child"));
        var stateStore4 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, null, null, null));
        var engine4 = new StorageMigrationEngine(stateStore4);
        var r4 = engine4.Execute(new StorageMigrationRequest(source, childTarget, Guid.NewGuid()));
        Assert(!r4.Succeeded, "target 是 source 子目录应拒绝");

        // Relative/UNC → 拒绝（state 不用 pending，直接配 blank state）
        var stateStore5 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, null, null, null));
        var engine5 = new StorageMigrationEngine(stateStore5);
        var r5 = engine5.Execute(new StorageMigrationRequest(source, @"\\server\share\target", Guid.NewGuid()));
        Assert(!r5.Succeeded && r5.Error == StorageMigrationError.InvalidTarget,
            "UNC target 应拒绝");

        // 空间不足（注入探针）
        var stateStore6 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, target, migId, null));
        var spaceProbe = new MemorySpaceProbe { ResultOverride = false };
        var engine6 = new StorageMigrationEngine(stateStore6, spaceProbe: spaceProbe);
        var r6 = engine6.Execute(new StorageMigrationRequest(source, target, migId));
        Assert(!r6.Succeeded && r6.Error == StorageMigrationError.InsufficientSpace,
            "空间不足应返回 InsufficientSpace");
    }

    private static void RunMigrationFaultPointMatrix(SelfTestDirectoryTracker tracker)
    {
        var faultPoints = new[]
        {
            StorageMigrationFaultPoint.BeforeCheckpoint,
            StorageMigrationFaultPoint.AfterCheckpoint,
            StorageMigrationFaultPoint.AfterMarkerCreated,
            StorageMigrationFaultPoint.DuringCopy,
            StorageMigrationFaultPoint.AfterCopy,
            StorageMigrationFaultPoint.AfterRebase,
            StorageMigrationFaultPoint.AfterVerify,
            StorageMigrationFaultPoint.BeforePromote,
        };

        foreach (var fp in faultPoints)
        {
            string source = tracker.Track(CreateTestSource($"mig-fault-{fp}-"));
            string target = Path.Combine(Path.GetTempPath(), $"Clipora_SelfTest_Target_fault_{fp}_" + Guid.NewGuid().ToString("N"));
            // Don't track target — it may/may not be created
            var migId = Guid.NewGuid();
            var stateStore = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, target, migId, null));
            var faultInjector = new MemoryStorageMigrationFaultInjector { FailAt = fp };
            var engine = new StorageMigrationEngine(stateStore, faultInjector);

            try
            {
                var result = engine.Execute(new StorageMigrationRequest(source, target, migId));
                Assert(!result.Succeeded, $"故障点 {fp} 应失败");
                // source must be preserved
                Assert(Directory.Exists(source) && File.Exists(Path.Combine(source, "clipora.db")),
                    $"故障点 {fp}: source 应保留");

                // 这些故障点都在 promote 之前（§10「marker 创建前」/「marker 创建后至 promote 前」）：
                // target 不得存在；pending/id 必须清除；app-owned staging 必须删除。
                Assert(!Directory.Exists(target), $"故障点 {fp}: target 不应存在");
                var st = stateStore.Read();
                Assert(st.PendingRoot is null, $"故障点 {fp}: pending 应清除");
                Assert(st.MigrationId is null, $"故障点 {fp}: MigrationId 应清除");
                Assert(!Directory.Exists(GetStagingFromTarget(target, migId)),
                    $"故障点 {fp}: app-owned staging 应删除");
            }
            finally
            {
                try { Directory.Delete(target, true); } catch { /* ignore */ }
                string staging = GetStagingFromTarget(target, migId);
                try { Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }
    }

    private static void RunMigrationRecoverySemantics(SelfTestDirectoryTracker tracker)
    {
        // 场景：promotion 后 switch 失败 → target/pending 保留，第二次只验证+commit
        string source = tracker.Track(CreateTestSource("mig-recover-src-"));
        string target = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_recover_" + Guid.NewGuid().ToString("N"));
        var migId = Guid.NewGuid();
        var stateStore1 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, target, migId, null));
        var faultInjector = new MemoryStorageMigrationFaultInjector { FailAt = StorageMigrationFaultPoint.AfterPromoteBeforeSwitch };
        var engine1 = new StorageMigrationEngine(stateStore1, faultInjector);

        var r1 = engine1.Execute(new StorageMigrationRequest(source, target, migId));
        Assert(!r1.Succeeded, "promotion 后 switch 故障应失败");
        Assert(Directory.Exists(target), "target 应保留（已提升）");
        Assert(stateStore1.Read().PendingRoot is not null, "pending 应保留");

        // 第二次 run：验证 target + commit
        var stateStore2 = new MemoryStorageMigrationStateStore(stateStore1.Read());
        var engine2 = new StorageMigrationEngine(stateStore2);
        var r2 = engine2.Execute(new StorageMigrationRequest(source, target, migId));
        Assert(r2.Succeeded, $"恢复应成功: {r2.Detail}");
        Assert(string.Equals(Path.GetFullPath(stateStore2.Read().ActiveRoot), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "恢复后 ActiveRoot 应为 target");
        Assert(stateStore2.Read().PendingRoot is null, "恢复后 pending 应清除");

        // 清理 target（用于后续测试）
        try { Directory.Delete(target, true); } catch { /* ignore */ }

        // 场景：active==target + pending 残留 → 验证后清理
        string source2 = tracker.Track(CreateTestSource("mig-recover2-src-"));
        string target2 = tracker.Track(Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_recover2_" + Guid.NewGuid().ToString("N")));
        var migId2 = Guid.NewGuid();
        // 完整执行一次成功的迁移
        var stateStoreS1 = new MemoryStorageMigrationStateStore(new StorageMigrationState(source2, target2, migId2, null));
        var engineS1 = new StorageMigrationEngine(stateStoreS1);
        var rS1 = engineS1.Execute(new StorageMigrationRequest(source2, target2, migId2));
        Assert(rS1.Succeeded, "恢复场景预迁移应成功");

        // 模拟：state 已切换 target 但 pending 残留（人工制造）
        var dirtyState = new StorageMigrationState(target2, target2, migId2, source2);
        var stateStoreR = new MemoryStorageMigrationStateStore(dirtyState);
        var engineR = new StorageMigrationEngine(stateStoreR);
        var rR = engineR.Execute(new StorageMigrationRequest(source2, target2, migId2));
        Assert(rR.Succeeded, "active==target+pending 残留恢复应成功");
        Assert(stateStoreR.Read().PendingRoot is null, "恢复后 pending 应清除");
    }

    private static void RunMigrationProgressIsolation(SelfTestDirectoryTracker tracker)
    {
        string source = tracker.Track(CreateTestSource("mig-prog-"));
        string target = tracker.Track(Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_prog_" + Guid.NewGuid().ToString("N")));
        var migId = Guid.NewGuid();

        var stateStore = new MemoryStorageMigrationStateStore(new StorageMigrationState(source, target, migId, null));
        var engine = new StorageMigrationEngine(stateStore);

        // 用同步抛异常的 IProgress：引擎的同步 Report try/catch 应吞掉它，迁移不受影响。
        // 不用 Progress<T>——它把回调异步派发到 ThreadPool，抛异常会升级为进程崩溃（旧实现的真实缺陷）。
        var throwingProgress = new SyncThrowingProgress();

        var result = engine.Execute(new StorageMigrationRequest(source, target, migId), throwingProgress);

        Assert(result.Succeeded, $"回调抛异常不应破坏迁移: {result.Detail}");
        Assert(throwingProgress.Threw, "回调应已触发异常");
        Assert(throwingProgress.CallCount >= 1, "回调应被调用");
        Assert(Directory.Exists(target), "target 应存在");
    }

    /// <summary>同步抛异常的进度回调：首次 Report 即抛，验证引擎隔离回调异常。同步执行，不经 ThreadPool（避免崩进程）。</summary>
    private sealed class SyncThrowingProgress : IProgress<StorageMigrationProgress>
    {
        public int CallCount;
        public bool Threw;

        public void Report(StorageMigrationProgress value)
        {
            CallCount++;
            if (CallCount == 1)
            {
                Threw = true;
                throw new InvalidOperationException("progress callback throws");
            }
        }
    }

    private static string CreateTestSource(string prefix, string? basePath = null)
    {
        string root = Path.Combine(basePath ?? Path.GetTempPath(), "Clipora_SelfTest_" + prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // 子目录
        string images = Path.Combine(root, "images");
        string files = Path.Combine(root, "files");
        string payloads = Path.Combine(files, "payloads");
        string manifests = Path.Combine(files, "manifests");
        string richtext = Path.Combine(root, "richtext");
        string thumbs = Path.Combine(root, "thumbs");
        string icons = Path.Combine(root, "icons");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(payloads);
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(richtext);
        Directory.CreateDirectory(thumbs);
        Directory.CreateDirectory(icons);

        // settings.json
        string settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, @"{""RetentionDays"":3,""Language"":""zh-CN""}");

        // 附件文件
        string imageFile = Path.Combine(images, "test.png");
        File.WriteAllText(imageFile, "fake-image-bytes");
        string thumbFile = Path.Combine(thumbs, "test-thumb.png");
        File.WriteAllText(thumbFile, "fake-thumb-bytes");
        string iconFile = Path.Combine(icons, "app.ico");
        File.WriteAllText(iconFile, "fake-icon");
        string richFile = Path.Combine(richtext, "sample.rtf");
        File.WriteAllText(richFile, @"{\rtf1\b hello\b0}");
        string payloadFile = Path.Combine(payloads, "doc.txt");
        File.WriteAllText(payloadFile, "file payload content");

        // 文件 manifest（非引用）
        string manifPath = Path.Combine(manifests, "doc.clipora-files.json");
        var manifest = new ClipFileManifest
        {
            IsReferenceOnly = false,
            Entries =
            {
                new ClipFileManifestEntry
                {
                    OriginalPath = Path.Combine(Path.GetTempPath(), "user-original.txt"),
                    StoredPath = payloadFile,
                    DisplayName = "doc.txt",
                    SizeBytes = new FileInfo(payloadFile).Length,
                },
            },
        };
        manifest.Save(manifPath);

        // 额外的普通文件
        string extraFile = Path.Combine(root, "extra.txt");
        File.WriteAllText(extraFile, "extra content");

        // 构建 SQLite 数据库 — 使用非池化连接并立即关闭，确保无残留句柄
        string dbPath = Path.Combine(root, "clipora.db");
        string cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS clip_items (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Type INTEGER NOT NULL, PreviewText TEXT NOT NULL DEFAULT '',
                    TextContent TEXT, RefPath TEXT, ThumbnailPath TEXT, SourceApp TEXT, SourceIconPath TEXT,
                    CreatedAt TEXT NOT NULL, IsPinned INTEGER NOT NULL DEFAULT 0, ContentHash TEXT NOT NULL DEFAULT '',
                    SizeBytes INTEGER NOT NULL DEFAULT 0, IsDeleted INTEGER NOT NULL DEFAULT 0, DeletedAt TEXT);
                CREATE TABLE IF NOT EXISTS tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Color TEXT NOT NULL DEFAULT '#0078D4',
                    SortOrder INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS clip_item_tags (
                    ClipItemId INTEGER NOT NULL, TagId INTEGER NOT NULL,
                    PRIMARY KEY (ClipItemId, TagId),
                    FOREIGN KEY (ClipItemId) REFERENCES clip_items(Id) ON DELETE CASCADE,
                    FOREIGN KEY (TagId) REFERENCES tags(Id) ON DELETE CASCADE);
                """;
            cmd.ExecuteNonQuery();

            // 插入测试数据
            string now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO clip_items (Type, PreviewText, TextContent, RefPath, ThumbnailPath, SourceIconPath, CreatedAt, ContentHash, SizeBytes)
                VALUES
                (0, 'text item', 'hello world', NULL, NULL, NULL, $now, 'hash1', 11),
                (5, 'image item', NULL, $img, $thumb, NULL, $now, 'hash2', 200),
                (6, 'file item', NULL, $manif, NULL, $icon, $now, 'hash3', 50),
                (3, 'rich item', 'rich text', $rich, NULL, NULL, $now, 'hash4', 30);
                """;
            ins.Parameters.AddWithValue("$now", now);
            ins.Parameters.AddWithValue("$img", imageFile);
            ins.Parameters.AddWithValue("$thumb", thumbFile);
            ins.Parameters.AddWithValue("$manif", manifPath);
            ins.Parameters.AddWithValue("$icon", iconFile);
            ins.Parameters.AddWithValue("$rich", richFile);
            ins.ExecuteNonQuery();

            // 插入标签和关联
            using var tagIns = conn.CreateCommand();
            tagIns.CommandText = "INSERT INTO tags (Name, Color) VALUES ('test', '#0078D4');";
            tagIns.ExecuteNonQuery();
            using var ctIns = conn.CreateCommand();
            ctIns.CommandText = "INSERT INTO clip_item_tags (ClipItemId, TagId) VALUES (1, 1);";
            ctIns.ExecuteNonQuery();

            // checkpoint 并关闭 WAL
            using var cpCmd = conn.CreateCommand();
            cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cpCmd.ExecuteNonQuery();
            conn.Close();
        }
        // 清理 WAL/SHM 文件（若残留）
        try { File.Delete(Path.Combine(root, "clipora.db-wal")); } catch { /* ignore */ }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        return root;
    }

    private static (int ClipCount, string AttachHash) TakeSourceSnapshot(string source)
    {
        // 迁移前 source 快照：clip_items 行数 + 附件文件聚合 SHA-256
        using var sha = System.Security.Cryptography.SHA256.Create();
        var attachFiles = new List<string>();

        foreach (string sub in new[] { "images", "thumbs", "icons", "richtext" })
        {
            string dir = Path.Combine(source, sub);
            if (Directory.Exists(dir))
                attachFiles.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f));
        }
        // payloads + manifests
        string filesDir = Path.Combine(source, "files");
        if (Directory.Exists(filesDir))
            attachFiles.AddRange(Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories).OrderBy(f => f));

        attachFiles.AddRange(Directory.GetFiles(source, "*").Where(f =>
            !f.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f));

        foreach (string f in attachFiles)
        {
            try { byte[] bytes = File.ReadAllBytes(f); sha.TransformBlock(bytes, 0, bytes.Length, null, 0); } catch { }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        int clipCount;
        try
        {
            string dbPath = Path.Combine(source, "clipora.db");
            string cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString();
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM clip_items;";
            clipCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            conn.Close();
        }
        catch { clipCount = -1; }

        return (clipCount, Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()));
    }

    private static string GetStagingFromTarget(string targetRoot, Guid migrationId)
    {
        string? parent = Path.GetDirectoryName(targetRoot) ?? string.Empty;
        string targetName = Path.GetFileName(targetRoot);
        return Path.Combine(parent, $".{targetName}.clipora-migrating-{migrationId:N}");
    }

    // ── RegistryStorageMigrationStateStore 自检（M4.2.3a-1） ──────────────────────

    private static void RunRegistryStateStoreTests(SelfTestDirectoryTracker dirTracker)
    {
        // 快照生产键：跑前（§6.10）
        string? prodDataRootBefore = ReadProductionDataRoot();
        bool prodKeyExistedBefore = ProductionStorageKeyExists();

        // 收集隔离测试键后缀，用于 finally 清理
        var testKeySuffixes = new System.Collections.Generic.List<string>();

        try
        {
            // —— 1. 空状态：无任何值 ——
            RunRegistryTest_EmptyState(testKeySuffixes, dirTracker);

            // —— 2. DataRoot 已设 ——
            RunRegistryTest_DataRootSet(testKeySuffixes, dirTracker);

            // —— 3. Enqueue 往返 ——
            RunRegistryTest_Enqueue(testKeySuffixes, dirTracker);

            // —— 4. CommitTarget 往返 ——
            RunRegistryTest_CommitTarget(testKeySuffixes, dirTracker);

            // —— 5. 从默认 Root 首次迁移 ——
            RunRegistryTest_FirstMigrationFromDefault(testKeySuffixes, dirTracker);

            // —— 6. ClearPending ——
            RunRegistryTest_ClearPending(testKeySuffixes, dirTracker);

            // —— 7. 崩溃形态映射 ——
            RunRegistryTest_CrashMorphology(testKeySuffixes, dirTracker);

            // —— 8. MigrationId 非法 ——
            RunRegistryTest_InvalidMigrationId(testKeySuffixes, dirTracker);

            // —— 9. 值类型错误 ——
            RunRegistryTest_ValueTypeError(testKeySuffixes, dirTracker);
        }
        finally
        {
            // 清理所有隔离测试子键
            foreach (string suffix in testKeySuffixes)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        @"Software\Clipora_SelfTest_Migration_" + suffix,
                        throwOnMissingSubKey: false);
                }
                catch { /* best-effort cleanup */ }
            }

            // §6.10：生产键零副作用断言
            string? prodDataRootAfter = ReadProductionDataRoot();
            bool prodKeyExistedAfter = ProductionStorageKeyExists();

            Assert(prodKeyExistedBefore == prodKeyExistedAfter,
                "生产 Storage 键存在性应在自检前后一致");
            Assert(string.Equals(prodDataRootBefore, prodDataRootAfter, StringComparison.Ordinal),
                $"生产 DataRoot 应在自检前后不变（before={prodDataRootBefore ?? "(null)"}，after={prodDataRootAfter ?? "(null)"}）");
        }
    }

    private static string? ReadProductionDataRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StorageRegistryKeys.KeyPath, writable: false);
            if (key is null)
                return null;
            string? value = key.GetValue(StorageRegistryKeys.ValueNameDataRoot) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null; // 不存在视为 null
        }
    }

    private static bool ProductionStorageKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StorageRegistryKeys.KeyPath, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建隔离测试键的完整 HKCU 子键路径，并在 testKeySuffixes 中登记以便 finally 清理。</summary>
    private static string CreateTestKeyPath(System.Collections.Generic.List<string> suffixes)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        suffixes.Add(suffix);
        return @"Software\Clipora_SelfTest_Migration_" + suffix;
    }

    private static string CreateTestDefaultRoot(SelfTestDirectoryTracker? tracker = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "clipora-selftest-registry-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tracker?.Track(dir);
        return dir;
    }

    // ── 个案 ────────────────────────────────────────────────────────

    private static void RunRegistryTest_EmptyState(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);

        StorageMigrationState state = store.Read();
        Assert(string.Equals(state.ActiveRoot, Path.GetFullPath(defaultRoot), StringComparison.OrdinalIgnoreCase),
            "空状态：ActiveRoot 应为 defaultActiveRoot");
        Assert(state.PendingRoot is null,
            "空状态：PendingRoot 应为 null");
        Assert(state.MigrationId is null,
            "空状态：MigrationId 应为 null");
        Assert(state.LastSourceRoot is null,
            "空状态：LastSourceRoot 应为 null");
    }

    private static void RunRegistryTest_DataRootSet(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string customRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-custom-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(customRoot);
        dirTracker.Track(customRoot);

        // 直接写注册表
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, customRoot, RegistryValueKind.String);
        }

        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        StorageMigrationState state = store.Read();
        Assert(string.Equals(state.ActiveRoot, Path.GetFullPath(customRoot), StringComparison.OrdinalIgnoreCase),
            "DataRoot 已设：ActiveRoot 应为自定义路径");
        Assert(state.PendingRoot is null,
            "DataRoot 已设：PendingRoot 仍应为 null");
    }

    private static void RunRegistryTest_Enqueue(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string targetRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-target-" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(targetRoot);
        var migrationId = Guid.NewGuid();

        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        store.Enqueue(targetRoot, migrationId);

        StorageMigrationState state = store.Read();
        Assert(string.Equals(state.PendingRoot!, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
            "Enqueue：PendingRoot 应为 target");
        Assert(state.MigrationId == migrationId,
            "Enqueue：MigrationId 应匹配");
        Assert(string.Equals(state.ActiveRoot, Path.GetFullPath(defaultRoot), StringComparison.OrdinalIgnoreCase),
            "Enqueue：ActiveRoot 应不变（仍是 default）");
        Assert(state.LastSourceRoot is null,
            "Enqueue：LastSourceRoot 应不变（null）");
    }

    private static void RunRegistryTest_CommitTarget(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string sourceRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-source-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        dirTracker.Track(sourceRoot);
        dirTracker.Track(targetRoot);
        var migrationId = Guid.NewGuid();

        // 先写入初始 DataRoot = source，模拟已迁移过的状态
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, sourceRoot, RegistryValueKind.String);
        }

        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        store.Enqueue(targetRoot, migrationId);
        store.CommitTarget(targetRoot, sourceRoot, migrationId);

        StorageMigrationState state = store.Read();
        Assert(string.Equals(state.ActiveRoot, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
            "CommitTarget：ActiveRoot 应为 target");
        Assert(state.PendingRoot is null,
            "CommitTarget：PendingRoot 应清除");
        Assert(state.MigrationId is null,
            "CommitTarget：MigrationId 应清除");
        Assert(string.Equals(state.LastSourceRoot!, Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase),
            "CommitTarget：LastSourceRoot 应为 source");

        // 验证底层注册表值
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
        {
            Assert(key is not null, "CommitTarget：注册表键应存在");
            string? dataRoot = key!.GetValue(StorageRegistryKeys.ValueNameDataRoot) as string;
            Assert(string.Equals(dataRoot, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
                "CommitTarget：底层 DataRoot 应为 target");
            // PendingRoot 和 MigrationId 应已删除
            bool hasPending = Array.Exists(key.GetValueNames(),
                n => string.Equals(n, StorageRegistryKeys.ValueNamePendingRoot, StringComparison.OrdinalIgnoreCase));
            Assert(!hasPending, "CommitTarget：底层 PendingRoot 应已删除");
            bool hasMigId = Array.Exists(key.GetValueNames(),
                n => string.Equals(n, StorageRegistryKeys.ValueNameMigrationId, StringComparison.OrdinalIgnoreCase));
            Assert(!hasMigId, "CommitTarget：底层 MigrationId 应已删除");
        }
    }

    private static void RunRegistryTest_FirstMigrationFromDefault(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string targetRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-target-" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(targetRoot);
        var migrationId = Guid.NewGuid();

        // 初始无 DataRoot（空状态）
        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        store.Enqueue(targetRoot, migrationId);

        // 用 source = defaultRoot 提交
        store.CommitTarget(targetRoot, defaultRoot, migrationId);

        StorageMigrationState state = store.Read();
        Assert(string.Equals(state.ActiveRoot, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
            "从默认 Root 首次迁移：ActiveRoot 应为 target");
        Assert(state.PendingRoot is null,
            "从默认 Root 首次迁移：PendingRoot 应清除");

        // 底层 DataRoot 应被创建
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
        {
            Assert(key is not null, "从默认 Root 首次迁移：注册表键应存在");
            string? dataRoot = key!.GetValue(StorageRegistryKeys.ValueNameDataRoot) as string;
            Assert(!string.IsNullOrWhiteSpace(dataRoot),
                "从默认 Root 首次迁移：DataRoot 应存在");
        }
    }

    private static void RunRegistryTest_ClearPending(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string targetRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-target-" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(targetRoot);
        var migrationId = Guid.NewGuid();

        // 先写 DataRoot = defaultRoot 模拟有数据的源
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, defaultRoot, RegistryValueKind.String);
        }

        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        store.Enqueue(targetRoot, migrationId);

        // 确认已入队
        StorageMigrationState enqueued = store.Read();
        Assert(enqueued.PendingRoot is not null && enqueued.MigrationId == migrationId,
            "ClearPending 准备：应已入队");

        // 用正确的 migrationId 清除
        store.ClearPending(migrationId);

        StorageMigrationState after = store.Read();
        Assert(after.PendingRoot is null,
            "ClearPending：PendingRoot 应删除");
        Assert(after.MigrationId is null,
            "ClearPending：MigrationId 应删除");
        // DataRoot 不动
        Assert(string.Equals(after.ActiveRoot, Path.GetFullPath(defaultRoot), StringComparison.OrdinalIgnoreCase),
            "ClearPending：DataRoot 应不动");

        // 用错误的 migrationId 应抛
        bool threwWrongId = false;
        try
        {
            store.ClearPending(Guid.NewGuid());
        }
        catch (StorageLocationException)
        {
            threwWrongId = true;
        }
        Assert(threwWrongId,
            "ClearPending(其他 id)：应抛 StorageLocationException");
    }

    private static void RunRegistryTest_CrashMorphology(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string sourceRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-source-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        dirTracker.Track(sourceRoot);
        dirTracker.Track(targetRoot);
        var migrationId = Guid.NewGuid();

        // 形态 1：DataRoot==PendingRoot==target（已提交但残留未清）
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, targetRoot, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNamePendingRoot, targetRoot, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, migrationId.ToString("D"), RegistryValueKind.String);
        }

        var store1 = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        StorageMigrationState state1 = store1.Read();
        Assert(string.Equals(state1.ActiveRoot, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
            "崩溃形态 1（已提交）：ActiveRoot==target");
        Assert(string.Equals(state1.PendingRoot!, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
            "崩溃形态 1（已提交）：PendingRoot==target");
        Assert(state1.MigrationId == migrationId,
            "崩溃形态 1（已提交）：MigrationId 存在");

        // 形态 2：DataRoot==source、PendingRoot==target（Path 2b）
        // 清理重来
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, sourceRoot, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNamePendingRoot, targetRoot, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, migrationId.ToString("D"), RegistryValueKind.String);
        }

        var store2 = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        StorageMigrationState state2 = store2.Read();
        Assert(string.Equals(state2.ActiveRoot, Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase),
            "崩溃形态 2（未提交）：ActiveRoot==source");
        Assert(string.Equals(state2.PendingRoot!, Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase),
            "崩溃形态 2（未提交）：PendingRoot==target");
        Assert(state2.MigrationId == migrationId,
            "崩溃形态 2（未提交）：MigrationId 存在");
    }

    private static void RunRegistryTest_InvalidMigrationId(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        // 写入非 GUID 字符串
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, "not-a-guid", RegistryValueKind.String);
        }

        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        StorageMigrationState state = store.Read();
        Assert(state.MigrationId is null,
            "MigrationId 非法：非 GUID 串应返回 null");
        // 不应抛异常
    }

    private static void RunRegistryTest_ValueTypeError(System.Collections.Generic.List<string> suffixes, SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        // 写入 REG_DWORD 而非 REG_SZ
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, 42, RegistryValueKind.DWord);
        }

        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        bool threw = false;
        try
        {
            store.Read();
        }
        catch (StorageLocationException ex)
        {
            threw = true;
            Assert(ex.ErrorCode == StorageLocationError.InvalidPath,
                "值类型错误应抛 InvalidPath");
        }
        Assert(threw,
            "值类型错误应抛 StorageLocationException（不可静默回退）");
    }

    // ── StorageMigrationStartupCoordinator 自检（M4.2.3a-2） ──────────────────

    private static void RunStartupCoordinatorTests(
        SelfTestDirectoryTracker dirTracker,
        SelfTestDirectoryTracker registryDirTracker)
    {
        // 快照生产键：跑前（§5.10）
        string? prodDataRootBefore = ReadProductionDataRoot();
        bool prodKeyExistedBefore = ProductionStorageKeyExists();

        // 收集隔离测试键后缀 + 临时目录，用于 finally 清理
        var testKeySuffixes = new System.Collections.Generic.List<string>();

        try
        {
            // —— 1. 无 pending ——
            RunCoordinatorTest_NoPending();

            // —— 2. !canMigrate 闸门 ——
            RunCoordinatorTest_CannotMigrate(testKeySuffixes, dirTracker);

            // —— 3. 全新迁移（form C） ——
            RunCoordinatorTest_FreshMigration(testKeySuffixes, dirTracker);

            // —— 4. 恢复 form A（已提交残留） ——
            RunCoordinatorTest_RecoveryFormA(testKeySuffixes, dirTracker);

            // —— 5. 恢复 form B（已提升未提交） ——
            RunCoordinatorTest_RecoveryFormB(testKeySuffixes, dirTracker);

            // —— 6. 失败 ——
            RunCoordinatorTest_Failure(testKeySuffixes, dirTracker);

            // —— 7. 幂等 ——
            RunCoordinatorTest_Idempotent(testKeySuffixes, dirTracker);

            // —— 8. 异常隔离 ——
            RunCoordinatorTest_ExceptionIsolation(dirTracker);

            // —— 8b. 引擎异常后保留首次已知 source/target ——
            RunCoordinatorTest_EngineExceptionKeepsKnownState();

            // —— 9. 现有全部 selftest 继续通过（由 Run() 内调用顺序保证） ——
            // —— 10. 生产键零副作用（finally 中断言） ——
        }
        finally
        {
            // 清理隔离测试子键
            foreach (string suffix in testKeySuffixes)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        @"Software\Clipora_SelfTest_Migration_" + suffix,
                        throwOnMissingSubKey: false);
                }
                catch { /* best-effort cleanup */ }
            }

            // §5.10：生产键零副作用
            string? prodDataRootAfter = ReadProductionDataRoot();
            bool prodKeyExistedAfter = ProductionStorageKeyExists();

            Assert(prodKeyExistedBefore == prodKeyExistedAfter,
                "协调器自检：生产 Storage 键存在性应在前后一致");
            Assert(string.Equals(prodDataRootBefore, prodDataRootAfter, StringComparison.Ordinal),
                $"协调器自检：生产 DataRoot 应不变（before={prodDataRootBefore ?? "(null)"}，after={prodDataRootAfter ?? "(null)"}）");
        }
    }

    // ── 个案 ────────────────────────────────────────────────────────

    /// <summary>§5.1 无 pending：空状态 → ProcessPending(true) → None。</summary>
    private static void RunCoordinatorTest_NoPending()
    {
        var engine = new SpyEngine();
        var stateStore = new MemoryStorageMigrationStateStore(
            new StorageMigrationState(@"C:\dummy", null, null, null));
        var coordinator = new StorageMigrationStartupCoordinator(stateStore, engine);

        var result = coordinator.ProcessPending(true);
        Assert(result.Action == StorageMigrationStartupAction.None,
            "无 pending：Action 应为 None");
        Assert(engine.CallCount == 0,
            "无 pending：引擎不应被调用");
        Assert(result.SourceRoot is null && result.TargetRoot is null,
            "无 pending：SourceRoot/TargetRoot 应为 null");
    }

    /// <summary>§5.2 !canMigrate 闸门：即使已 Enqueue，ProcessPending(false) → None。</summary>
    private static void RunCoordinatorTest_CannotMigrate(
        System.Collections.Generic.List<string> suffixes,
        SelfTestDirectoryTracker dirTracker)
    {
        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);
        string targetRoot = Path.Combine(Path.GetTempPath(), "clipora-selftest-target-" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(targetRoot);
        var migrationId = Guid.NewGuid();

        // 先 Enqueue（模拟用户点了"迁移并重启"）
        var store = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        store.Enqueue(targetRoot, migrationId);

        // canMigrate=false → 不读 state，直接 None
        var engine = new SpyEngine();
        var coordinator = new StorageMigrationStartupCoordinator(store, engine);
        var result = coordinator.ProcessPending(false);

        Assert(result.Action == StorageMigrationStartupAction.None,
            "!canMigrate：Action 应为 None");
        Assert(engine.CallCount == 0,
            "!canMigrate：引擎不应被调用");

        // registry 应不变（PendingRoot 仍存在）
        StorageMigrationState state = store.Read();
        Assert(state.PendingRoot is not null,
            "!canMigrate：PendingRoot 应保留不变");
        Assert(state.MigrationId == migrationId,
            "!canMigrate：MigrationId 应保留不变");
    }

    /// <summary>§5.3 全新迁移（form C）：真实 source + Enqueue → Completed。</summary>
    private static void RunCoordinatorTest_FreshMigration(
        System.Collections.Generic.List<string> suffixes,
        SelfTestDirectoryTracker dirTracker)
    {
        string source = dirTracker.Track(CreateTestSource("coord-fresh-src-"));
        string target = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_coord_fresh_" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(target);
        var migrationId = Guid.NewGuid();

        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        // 写入 DataRoot=source，再 Enqueue
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, source, RegistryValueKind.String);
        }
        var stateStore = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        stateStore.Enqueue(target, migrationId);

        var engine = new StorageMigrationEngine(stateStore);
        var coordinator = new StorageMigrationStartupCoordinator(stateStore, engine);

        var result = coordinator.ProcessPending(true);
        Assert(result.Action == StorageMigrationStartupAction.Completed,
            $"全新迁移：应为 Completed，实际={result.Action} detail={result.Detail}");
        Assert(string.Equals(result.ActiveRoot, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "全新迁移：ActiveRoot 应为 target");
        Assert(result.Error == StorageMigrationError.None,
            "全新迁移：Error 应为 None");
        Assert(string.Equals(result.SourceRoot!, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase),
            $"全新迁移：SourceRoot 应为 source（期望={source}，实际={result.SourceRoot}）");
        Assert(string.Equals(result.TargetRoot!, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            $"全新迁移：TargetRoot 应为 target（期望={target}，实际={result.TargetRoot}）");

        // registry 已提交
        StorageMigrationState final = stateStore.Read();
        Assert(string.Equals(final.ActiveRoot, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "全新迁移：registy ActiveRoot 应为 target");
        Assert(final.PendingRoot is null,
            "全新迁移：PendingRoot 应清除");
        Assert(final.MigrationId is null,
            "全新迁移：MigrationId 应清除");
        Assert(final.LastSourceRoot is not null,
            "全新迁移：LastSourceRoot 应有值");

        Assert(Directory.Exists(source), "全新迁移：source 应保留");
        Assert(Directory.Exists(target), "全新迁移：target 应存在");
    }

    /// <summary>§5.4 恢复 form A（已提交残留）：DataRoot==Pending==target → Completed。</summary>
    private static void RunCoordinatorTest_RecoveryFormA(
        System.Collections.Generic.List<string> suffixes,
        SelfTestDirectoryTracker dirTracker)
    {
        // 先做一次成功迁移得到合法 target 和正确的 LastSourceRoot
        string source = dirTracker.Track(CreateTestSource("coord-forma-src-"));
        string target = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_coord_formA_" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(target);
        var migrationId = Guid.NewGuid();

        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, source, RegistryValueKind.String);
        }
        var stateStore1 = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        stateStore1.Enqueue(target, migrationId);
        var engine1 = new StorageMigrationEngine(stateStore1);
        var r1 = new StorageMigrationStartupCoordinator(stateStore1, engine1);
        var result1 = r1.ProcessPending(true);
        Assert(result1.Action == StorageMigrationStartupAction.Completed,
            $"Form A 准备：第一次迁移应成功 detail={result1.Detail}");

        // 模拟崩溃：DataRoot==Pending==target + MigrationId 残留（form A）
        // CommitTarget 先写 LastSourceRoot 再写 DataRoot，故 DataRoot==target 时 LastSourceRoot 已写
        string savedSource = stateStore1.Read().LastSourceRoot!;
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, target, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNamePendingRoot, target, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, migrationId.ToString("D"), RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameLastSourceRoot, savedSource, RegistryValueKind.String);
        }

        var stateStore2 = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        var engine2 = new StorageMigrationEngine(stateStore2);
        var coordinator2 = new StorageMigrationStartupCoordinator(stateStore2, engine2);
        var result2 = coordinator2.ProcessPending(true);

        Assert(result2.Action == StorageMigrationStartupAction.Completed,
            $"Form A 恢复：应为 Completed detail={result2.Detail}");
        Assert(string.Equals(result2.ActiveRoot, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "Form A 恢复：ActiveRoot 应为 target");
        Assert(string.Equals(result2.SourceRoot!, savedSource, StringComparison.OrdinalIgnoreCase),
            $"Form A 恢复：SourceRoot 应为 LastSourceRoot（期望={savedSource}，实际={result2.SourceRoot}）");
        Assert(string.Equals(result2.TargetRoot!, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "Form A 恢复：TargetRoot 应为 target");

        StorageMigrationState final = stateStore2.Read();
        Assert(string.Equals(final.ActiveRoot, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "Form A 恢复：DataRoot 仍应为 target");
        Assert(final.PendingRoot is null,
            "Form A 恢复：PendingRoot 应清除");
        Assert(final.MigrationId is null,
            "Form A 恢复：MigrationId 应清除");
    }

    /// <summary>§5.5 恢复 form B（已提升未提交）：DataRoot==source, Pending==target → Completed。</summary>
    private static void RunCoordinatorTest_RecoveryFormB(
        System.Collections.Generic.List<string> suffixes,
        SelfTestDirectoryTracker dirTracker)
    {
        // 先做一次成功迁移
        string source = dirTracker.Track(CreateTestSource("coord-formb-src-"));
        string target = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_coord_formB_" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(target);
        var migrationId = Guid.NewGuid();

        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, source, RegistryValueKind.String);
        }
        var stateStore1 = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        stateStore1.Enqueue(target, migrationId);
        var engine1 = new StorageMigrationEngine(stateStore1);
        var r1 = new StorageMigrationStartupCoordinator(stateStore1, engine1);
        var result1 = r1.ProcessPending(true);
        Assert(result1.Action == StorageMigrationStartupAction.Completed,
            $"Form B 准备：第一次迁移应成功 detail={result1.Detail}");

        // 模拟崩溃：DataRoot==source, Pending==target（commit 前崩溃，form B）
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, source, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNamePendingRoot, target, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, migrationId.ToString("D"), RegistryValueKind.String);
        }

        var stateStore2 = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        var engine2 = new StorageMigrationEngine(stateStore2);
        var coordinator2 = new StorageMigrationStartupCoordinator(stateStore2, engine2);
        var result2 = coordinator2.ProcessPending(true);

        Assert(result2.Action == StorageMigrationStartupAction.Completed,
            $"Form B 恢复：应为 Completed detail={result2.Detail}");
        Assert(string.Equals(result2.ActiveRoot, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "Form B 恢复：ActiveRoot 应为 target");
        Assert(string.Equals(result2.SourceRoot!, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase),
            $"Form B 恢复：SourceRoot 应为 ActiveRoot(source)（期望={source}，实际={result2.SourceRoot}）");
        Assert(string.Equals(result2.TargetRoot!, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "Form B 恢复：TargetRoot 应为 target");

        StorageMigrationState final = stateStore2.Read();
        Assert(string.Equals(final.ActiveRoot, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "Form B 恢复：DataRoot 应切换为 target");
        Assert(final.PendingRoot is null, "Form B 恢复：PendingRoot 应清除");
    }

    /// <summary>§5.6 失败：构造必失请求 → ProcessPending → Failed。</summary>
    private static void RunCoordinatorTest_Failure(
        System.Collections.Generic.List<string> suffixes,
        SelfTestDirectoryTracker dirTracker)
    {
        string source = dirTracker.Track(CreateTestSource("coord-fail-src-"));
        string target = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_coord_fail_" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(target);
        var migrationId = Guid.NewGuid();

        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, source, RegistryValueKind.String);
        }
        var stateStore = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);

        // 指向不存在的 source 目录 → 引擎必失败
        string missingSource = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_MissingSource_" + Guid.NewGuid().ToString("N"));
        // 不创建该目录

        var dirtyState = new StorageMigrationState(missingSource, target, migrationId, null);
        var memoryStore = new MemoryStorageMigrationStateStore(dirtyState);
        var engine = new StorageMigrationEngine(memoryStore);
        var coordinator = new StorageMigrationStartupCoordinator(memoryStore, engine);

        var result = coordinator.ProcessPending(true);
        Assert(result.Action == StorageMigrationStartupAction.Failed,
            $"失败：Action 应为 Failed detail={result.Detail}");
        Assert(string.Equals(result.ActiveRoot, missingSource, StringComparison.OrdinalIgnoreCase),
            "失败：ActiveRoot 应仍为 source");
        Assert(result.Error != StorageMigrationError.None,
            "失败：Error 不应为 None");
        Assert(string.Equals(result.SourceRoot!, missingSource, StringComparison.OrdinalIgnoreCase),
            "失败：SourceRoot 应为 missingSource");
        Assert(string.Equals(result.TargetRoot!, target, StringComparison.OrdinalIgnoreCase),
            "失败：TargetRoot 应为 target");

        // 原始 registry 不变（用的 memory store，不影响 registry）
    }

    /// <summary>§5.7 幂等：成功一次后再 ProcessPending → None。</summary>
    private static void RunCoordinatorTest_Idempotent(
        System.Collections.Generic.List<string> suffixes,
        SelfTestDirectoryTracker dirTracker)
    {
        string source = dirTracker.Track(CreateTestSource("coord-idem-src-"));
        string target = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Target_coord_idem_" + Guid.NewGuid().ToString("N"));
        dirTracker.Track(target);
        var migrationId = Guid.NewGuid();

        string keyPath = CreateTestKeyPath(suffixes);
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, source, RegistryValueKind.String);
        }
        var stateStore = new RegistryStorageMigrationStateStore(keyPath, defaultRoot);
        stateStore.Enqueue(target, migrationId);

        // 第一次
        var c1 = new StorageMigrationStartupCoordinator(stateStore, new StorageMigrationEngine(stateStore));
        var r1 = c1.ProcessPending(true);
        Assert(r1.Action == StorageMigrationStartupAction.Completed,
            $"幂等准备：第一次应成功 detail={r1.Detail}");

        // 第二次：pending 已清
        var c2 = new StorageMigrationStartupCoordinator(stateStore, new StorageMigrationEngine(stateStore));
        var r2 = c2.ProcessPending(true);
        Assert(r2.Action == StorageMigrationStartupAction.None,
            "幂等：第二次应返回 None（pending 已清）");
    }

    /// <summary>§5.8 异常隔离：注入会抛的组件 → ProcessPending 不抛、返回 Failed。</summary>
    private static void RunCoordinatorTest_ExceptionIsolation(SelfTestDirectoryTracker dirTracker)
    {
        string defaultRoot = CreateTestDefaultRoot(dirTracker);

        // 注入一个读时抛异常的 state store
        var throwingStore = new ThrowingStateStore(
            new StorageLocationException(StorageLocationError.AccessDenied, "模拟注册表访问失败"));
        var engine = new StorageMigrationEngine(new MemoryStorageMigrationStateStore(
            new StorageMigrationState(defaultRoot, null, null, null)));

        var coordinator = new StorageMigrationStartupCoordinator(throwingStore, engine);
        var result = coordinator.ProcessPending(true);

        Assert(result.Action == StorageMigrationStartupAction.Failed,
            "异常隔离：应为 Failed，不可抛异常");
        Assert(result.Error == StorageMigrationError.Unknown,
            "异常隔离：Error 应为 Unknown");
        Assert(result.Detail is not null && result.Detail.Contains("模拟注册表访问失败", StringComparison.Ordinal),
            "异常隔离：Detail 应包含原始异常消息");
        Assert(result.SourceRoot is null && result.TargetRoot is null,
            "异常隔离：state 不可读时 SourceRoot/TargetRoot 应为 null");

        // ProcessPending 自身不抛（已达这里即通过）
    }

    private static void RunCoordinatorTest_EngineExceptionKeepsKnownState()
    {
        string source = Path.Combine(Path.GetTempPath(), "clipora-selftest-known-source-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(Path.GetTempPath(), "clipora-selftest-known-target-" + Guid.NewGuid().ToString("N"));
        var state = new StorageMigrationState(source, target, Guid.NewGuid(), null);
        var store = new ReadOnceStateStore(state);
        var coordinator = new StorageMigrationStartupCoordinator(store, new ThrowingMigrationEngine());

        StorageMigrationStartupResult result = coordinator.ProcessPending(true);

        Assert(result.Action == StorageMigrationStartupAction.Failed,
            "引擎异常：应返回 Failed");
        Assert(store.ReadCount == 1,
            "引擎异常：catch 必须复用首次已知 state，不能依赖第二次读取");
        Assert(string.Equals(result.SourceRoot, source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(result.TargetRoot, target, StringComparison.OrdinalIgnoreCase),
            "引擎异常：应保留首次读取所映射的 source/target");
    }

    // ── 协调器自检用辅助类型 ────────────────────────────────────────

    /// <summary>调用计数桩：验证引擎是否被调用。</summary>
    private sealed class SpyEngine : IStorageMigrationEngine
    {
        public int CallCount { get; private set; }

        public StorageMigrationResult Execute(
            StorageMigrationRequest request,
            IProgress<StorageMigrationProgress>? progress = null)
        {
            CallCount++;
            return new StorageMigrationResult(
                false, StorageMigrationPhase.Failed, StorageMigrationError.Unknown,
                string.Empty, false, "spy engine — should not be called");
        }
    }

    /// <summary>会抛异常的 state store 桩：验证 ProcessPending 异常隔离。</summary>
    private sealed class ThrowingStateStore : IStorageMigrationStateStore
    {
        private readonly StorageLocationException _ex;
        public ThrowingStateStore(StorageLocationException ex) => _ex = ex;
        public StorageMigrationState Read() => throw _ex;
        public void ClearPending(Guid migrationId) => throw _ex;
        public void CommitTarget(string targetRoot, string sourceRoot, Guid migrationId) => throw _ex;
    }

    private sealed class ReadOnceStateStore : IStorageMigrationStateStore
    {
        private readonly StorageMigrationState _state;
        public ReadOnceStateStore(StorageMigrationState state) => _state = state;
        public int ReadCount { get; private set; }
        public StorageMigrationState Read()
        {
            ReadCount++;
            if (ReadCount > 1)
                throw new InvalidOperationException("state must not be read twice after engine failure");
            return _state;
        }
        public void ClearPending(Guid migrationId) { }
        public void CommitTarget(string targetRoot, string sourceRoot, Guid migrationId) { }
    }

    private sealed class ThrowingMigrationEngine : IStorageMigrationEngine
    {
        public StorageMigrationResult Execute(
            StorageMigrationRequest request,
            IProgress<StorageMigrationProgress>? progress = null) =>
            throw new IOException("模拟引擎异常");
    }

    // ── StorageMigrationRestartCoordinator 自检（M4.2.3a-2c） ────────────────

    private static void RunStorageMigrationRestartTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "clipora-selftest-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string executablePath = Path.Combine(root, "Clipora.exe");
            File.WriteAllBytes(executablePath, Array.Empty<byte>());
            string targetRoot = Path.Combine(root, "target");
            Guid migrationId = Guid.NewGuid();
            var pendingState = new StorageMigrationState(root, targetRoot, migrationId, null);

            StorageMigrationRestartCoordinator Create(
                SpyMigrationStateStore stateStore,
                SpyMigrationProcessLauncher launcher,
                bool canMigrate = true,
                bool isReleaseBuild = true,
                Func<string?>? processPathProvider = null) =>
                new(
                    stateStore,
                    launcher,
                    processPathProvider ?? (() => executablePath),
                    root,
                    canMigrate,
                    isReleaseBuild);

            // 1. 未请求：退出阶段不得启动。
            {
                var stateStore = new SpyMigrationStateStore(pendingState);
                var launcher = new SpyMigrationProcessLauncher();
                var coordinator = Create(stateStore, launcher);
                Assert(!coordinator.TryLaunchAfterExit(out string? error) && error is null,
                    "重启协调器：未请求时不应启动且不应报错");
                Assert(launcher.CallCount == 0, "重启协调器：未请求时 launcher 调用数应为 0");
            }

            // 2. Debug / canMigrate=false：在读取 state 前拒绝。
            {
                var debugStore = new SpyMigrationStateStore(pendingState);
                var debugLauncher = new SpyMigrationProcessLauncher();
                var debugCoordinator = Create(debugStore, debugLauncher, isReleaseBuild: false);
                Assert(!debugCoordinator.TryRequest(migrationId, out _), "重启协调器：Debug 应拒绝请求");
                Assert(debugStore.ReadCount == 0 && debugLauncher.CallCount == 0,
                    "重启协调器：Debug 拒绝前不得读 state 或启动进程");

                var fixedStore = new SpyMigrationStateStore(pendingState);
                var fixedLauncher = new SpyMigrationProcessLauncher();
                var fixedCoordinator = Create(fixedStore, fixedLauncher, canMigrate: false);
                Assert(!fixedCoordinator.TryRequest(migrationId, out _),
                    "重启协调器：canMigrate=false 应拒绝请求");
                Assert(fixedStore.ReadCount == 0 && fixedLauncher.CallCount == 0,
                    "重启协调器：canMigrate=false 拒绝前不得读 state 或启动进程");
            }

            // 3. 空 id / 无 pending / id 不匹配：全部拒绝。
            {
                var emptyIdStore = new SpyMigrationStateStore(pendingState);
                var emptyIdCoordinator = Create(emptyIdStore, new SpyMigrationProcessLauncher());
                Assert(!emptyIdCoordinator.TryRequest(Guid.Empty, out _),
                    "重启协调器：Guid.Empty 应拒绝");
                Assert(emptyIdStore.ReadCount == 0, "重启协调器：Guid.Empty 不应读取 state");

                var noPendingStore = new SpyMigrationStateStore(new StorageMigrationState(root, null, null, null));
                var noPendingCoordinator = Create(noPendingStore, new SpyMigrationProcessLauncher());
                Assert(!noPendingCoordinator.TryRequest(migrationId, out _),
                    "重启协调器：无 pending 应拒绝");

                var mismatchStore = new SpyMigrationStateStore(pendingState);
                var mismatchCoordinator = Create(mismatchStore, new SpyMigrationProcessLauncher());
                Assert(!mismatchCoordinator.TryRequest(Guid.NewGuid(), out _),
                    "重启协调器：pending id 不匹配应拒绝");
            }

            // 4/5. 匹配请求：同 id 幂等、不同 id 拒绝，退出阶段精确启动一次。
            {
                var stateStore = new SpyMigrationStateStore(pendingState);
                var launcher = new SpyMigrationProcessLauncher();
                var coordinator = Create(stateStore, launcher);
                Assert(coordinator.TryRequest(migrationId, out string? requestError) && requestError is null,
                    "重启协调器：匹配 pending/id 应接受请求");
                Assert(coordinator.TryRequest(migrationId, out _),
                    "重启协调器：同 id 重复请求应幂等成功");
                Assert(!coordinator.TryRequest(Guid.NewGuid(), out _),
                    "重启协调器：不同 id 二次请求应拒绝");
                Assert(coordinator.TryLaunchAfterExit(out string? launchError) && launchError is null,
                    "重启协调器：退出阶段应启动迁移完成进程");
                Assert(launcher.CallCount == 1 && launcher.LastStartInfo is not null,
                    "重启协调器：launcher 应精确调用一次");
                Assert(string.Equals(launcher.LastStartInfo!.FileName, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase),
                    "重启协调器：应启动当前 exe");
                Assert(!launcher.LastStartInfo.UseShellExecute,
                    "重启协调器：UseShellExecute 必须为 false");
                Assert(launcher.LastStartInfo.ArgumentList.Count == 1
                    && launcher.LastStartInfo.ArgumentList[0] == StorageMigrationRestartCoordinator.CompletionArgument,
                    "重启协调器：参数应精确为 --complete-storage-migration");
                Assert(!coordinator.TryLaunchAfterExit(out string? secondLaunchError) && secondLaunchError is null,
                    "重启协调器：重复退出调用不得再次启动");
                Assert(launcher.CallCount == 1, "重启协调器：重复退出后 launcher 仍应只调用一次");
                Assert(stateStore.MutationCount == 0 && stateStore.State == pendingState,
                    "重启协调器：请求/启动不得修改 pending state");
            }

            // 6. 无效 executable path：受控失败，不调用 launcher。
            string nonExePath = Path.Combine(root, "Clipora.dll");
            File.WriteAllBytes(nonExePath, Array.Empty<byte>());
            string?[] invalidPaths =
            {
                null,
                "Clipora.exe",
                Path.Combine(root, "missing.exe"),
                nonExePath,
            };
            foreach (string? invalidPath in invalidPaths)
            {
                var stateStore = new SpyMigrationStateStore(pendingState);
                var launcher = new SpyMigrationProcessLauncher();
                var coordinator = Create(stateStore, launcher, processPathProvider: () => invalidPath);
                Assert(coordinator.TryRequest(migrationId, out _),
                    "重启协调器：无效 exe 个案应先接受匹配请求");
                Assert(!coordinator.TryLaunchAfterExit(out string? error) && !string.IsNullOrWhiteSpace(error),
                    "重启协调器：无效 exe 应受控失败");
                Assert(launcher.CallCount == 0, "重启协调器：无效 exe 不得调用 launcher");
                Assert(stateStore.MutationCount == 0 && stateStore.State == pendingState,
                    "重启协调器：无效 exe 不得清 pending");
            }

            // 7. launcher 异常：受控失败，pending 原样保留。
            {
                var stateStore = new SpyMigrationStateStore(pendingState);
                var launcher = new SpyMigrationProcessLauncher(new InvalidOperationException("模拟启动失败"));
                var coordinator = Create(stateStore, launcher);
                Assert(coordinator.TryRequest(migrationId, out _),
                    "重启协调器：异常个案应先接受匹配请求");
                Assert(!coordinator.TryLaunchAfterExit(out string? error)
                    && error?.Contains("模拟启动失败", StringComparison.Ordinal) == true,
                    "重启协调器：launcher 异常应转受控错误");
                Assert(stateStore.MutationCount == 0 && stateStore.State == pendingState,
                    "重启协调器：launcher 异常不得清 pending");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class SpyMigrationStateStore : IStorageMigrationStateStore
    {
        public SpyMigrationStateStore(StorageMigrationState state) => State = state;

        public StorageMigrationState State { get; private set; }
        public int ReadCount { get; private set; }
        public int MutationCount { get; private set; }

        public StorageMigrationState Read()
        {
            ReadCount++;
            return State;
        }

        public void ClearPending(Guid migrationId)
        {
            MutationCount++;
            State = State with { PendingRoot = null, MigrationId = null };
        }

        public void CommitTarget(string targetRoot, string sourceRoot, Guid migrationId)
        {
            MutationCount++;
            State = new StorageMigrationState(targetRoot, null, null, sourceRoot);
        }
    }

    private sealed class SpyMigrationProcessLauncher : IStorageMigrationProcessLauncher
    {
        private readonly Exception? _exception;

        public SpyMigrationProcessLauncher(Exception? exception = null) => _exception = exception;

        public int CallCount { get; private set; }
        public ProcessStartInfo? LastStartInfo { get; private set; }

        public void Start(ProcessStartInfo startInfo)
        {
            CallCount++;
            LastStartInfo = startInfo;
            if (_exception is not null)
                throw _exception;
        }
    }

    // ── StorageMigrationPlanService 自检（M4.2.3a-3a） ──────────────────────

    private static void RunStorageMigrationPlanTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "Clipora_SelfTest_Plan_" + Guid.NewGuid().ToString("N"));
        string? prodDataRootBefore = ReadProductionDataRoot();
        bool prodKeyExistedBefore = ProductionStorageKeyExists();
        Directory.CreateDirectory(root);

        try
        {
            string source = Path.Combine(root, "Source");
            string parent = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(parent);
            File.WriteAllBytes(Path.Combine(source, "clipora.db"), new byte[40]);
            File.WriteAllBytes(Path.Combine(source, "payload.bin"), new byte[60]);
            File.WriteAllBytes(Path.Combine(source, "clipora.db-wal"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(source, "clipora.db-shm"), new byte[1000]);

            StorageMigrationPlanService Create(
                MemoryMigrationQueueStore? store = null,
                bool canMigrate = true,
                IStorageMigrationPlanFileSystem? fileSystem = null,
                ISpaceProbe? spaceProbe = null,
                string? sourceRoot = null) =>
                new(
                    sourceRoot ?? source,
                    canMigrate,
                    store ?? new MemoryMigrationQueueStore(new StorageMigrationState(sourceRoot ?? source, null, null, null)),
                    spaceProbe ?? new SpyPlanSpaceProbe(true),
                    fileSystem ?? new SpyPlanFileSystem());

            // 1. canMigrate=false：store / filesystem / space probe 全部零调用。
            {
                var store = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null));
                var fs = new SpyPlanFileSystem();
                var space = new SpyPlanSpaceProbe(true);
                var service = Create(store, canMigrate: false, fileSystem: fs, spaceProbe: space);
                StorageMigrationPlanResult result = service.Plan(parent);
                Assert(!result.Succeeded && result.Error == StorageMigrationPlanError.Unavailable,
                    "迁移计划：canMigrate=false 应立即 Unavailable");
                Assert(store.ReadCount == 0 && fs.CallCount == 0 && space.CallCount == 0,
                    "迁移计划：canMigrate=false 不得读取 store 或探测文件系统/空间");
            }

            // 2. 输入路径与网络路径拒绝。
            foreach (string? invalidParent in new[] { null, "", "relative", "C:relative" })
            {
                StorageMigrationPlanResult result = Create().Plan(invalidParent);
                Assert(!result.Succeeded && result.Error == StorageMigrationPlanError.InvalidParent,
                    $"迁移计划：非法父目录应拒绝 ({invalidParent ?? "null"})");
            }
            {
                var networkFs = new SpyPlanFileSystem { ForceNetwork = true };
                StorageMigrationPlanResult result = Create(fileSystem: networkFs).Plan(parent);
                Assert(!result.Succeeded && result.Error == StorageMigrationPlanError.UnsupportedNetworkPath,
                    "迁移计划：网络路径应拒绝");
            }

            // 3. source / db / parent / target 存在性。
            {
                string missingSource = Path.Combine(root, "MissingSource");
                var store = new MemoryMigrationQueueStore(new StorageMigrationState(missingSource, null, null, null));
                Assert(Create(store, sourceRoot: missingSource).Plan(parent).Error == StorageMigrationPlanError.SourceUnavailable,
                    "迁移计划：source 缺失应拒绝");

                string noDbSource = Path.Combine(root, "NoDbSource");
                Directory.CreateDirectory(noDbSource);
                var noDbStore = new MemoryMigrationQueueStore(new StorageMigrationState(noDbSource, null, null, null));
                Assert(Create(noDbStore, sourceRoot: noDbSource).Plan(parent).Error == StorageMigrationPlanError.SourceUnavailable,
                    "迁移计划：source 缺 clipora.db 应拒绝");

                Assert(Create().Plan(Path.Combine(root, "MissingParent")).Error == StorageMigrationPlanError.InvalidParent,
                    "迁移计划：父目录缺失应拒绝");

                string targetDirParent = Path.Combine(root, "TargetDirParent");
                Directory.CreateDirectory(Path.Combine(targetDirParent, "Clipora"));
                Assert(Create().Plan(targetDirParent).Error == StorageMigrationPlanError.TargetExists,
                    "迁移计划：target 目录已存在应拒绝");

                string targetFileParent = Path.Combine(root, "TargetFileParent");
                Directory.CreateDirectory(targetFileParent);
                File.WriteAllText(Path.Combine(targetFileParent, "Clipora"), "occupied");
                Assert(Create().Plan(targetFileParent).Error == StorageMigrationPlanError.TargetExists,
                    "迁移计划：target 被文件占用应拒绝");
            }

            // 4. 相同/父子关系拒绝；相似前缀但非父子允许。
            {
                string sameParent = Path.Combine(root, "SameParent");
                string sameSource = Path.Combine(sameParent, "Clipora");
                Directory.CreateDirectory(sameSource);
                File.WriteAllText(Path.Combine(sameSource, "clipora.db"), "db");
                var sameStore = new MemoryMigrationQueueStore(new StorageMigrationState(sameSource, null, null, null));
                Assert(Create(sameStore, sourceRoot: sameSource).Plan(sameParent).Error == StorageMigrationPlanError.TargetExists,
                    "迁移计划：target 与 source 相同应拒绝（已存在优先）");

                string childParent = Path.Combine(source, "ChildParent");
                Directory.CreateDirectory(childParent);
                Assert(Create().Plan(childParent).Error == StorageMigrationPlanError.SameOrNestedPath,
                    "迁移计划：target 位于 source 内应拒绝");

                string prefixSource = Path.Combine(root, "Alpha");
                string prefixParent = Path.Combine(root, "Alpha2");
                Directory.CreateDirectory(prefixSource);
                Directory.CreateDirectory(prefixParent);
                File.WriteAllText(Path.Combine(prefixSource, "clipora.db"), "db");
                var prefixStore = new MemoryMigrationQueueStore(new StorageMigrationState(prefixSource, null, null, null));
                Assert(Create(prefixStore, sourceRoot: prefixSource).Plan(prefixParent).Succeeded,
                    "迁移计划：相似前缀但非父子应允许");
            }

            // 5. source / parent / source tree reparse point 拒绝（由可注入 FS 稳定模拟）。
            {
                var sourceReparse = new SpyPlanFileSystem { ForcedReparsePath = source };
                Assert(Create(fileSystem: sourceReparse).Plan(parent).Error == StorageMigrationPlanError.ReparsePoint,
                    "迁移计划：source reparse point 应拒绝");

                var parentReparse = new SpyPlanFileSystem { ForcedReparsePath = parent };
                Assert(Create(fileSystem: parentReparse).Plan(parent).Error == StorageMigrationPlanError.ReparsePoint,
                    "迁移计划：parent reparse point 应拒绝");

                string nestedFile = Path.Combine(source, "payload.bin");
                var treeReparse = new SpyPlanFileSystem { ForcedReparsePath = nestedFile };
                Assert(Create(fileSystem: treeReparse).Plan(parent).Error == StorageMigrationPlanError.ReparsePoint,
                    "迁移计划：source tree reparse point 应拒绝");
            }

            // 6. 写探针 / 枚举 / 长度溢出均受控失败，探针无残留。
            {
                var probeFailure = new SpyPlanFileSystem { FailProbeCreation = true };
                StorageMigrationPlanResult result = Create(fileSystem: probeFailure).Plan(parent);
                Assert(!result.Succeeded && result.Error == StorageMigrationPlanError.AccessDenied,
                    "迁移计划：写探针失败应为 AccessDenied");
                Assert(!Directory.EnumerateFiles(parent, ".clipora-write-probe-*", SearchOption.TopDirectoryOnly).Any(),
                    "迁移计划：写探针失败后不得残留探针");

                var enumerateFailure = new SpyPlanFileSystem { FailEnumeration = true };
                Assert(!Create(fileSystem: enumerateFailure).Plan(parent).Succeeded,
                    "迁移计划：source 枚举失败应受控返回");

                var overflowFs = new SpyPlanFileSystem { FileLengthOverride = long.MaxValue };
                StorageMigrationPlanResult overflow = Create(fileSystem: overflowFs).Plan(parent);
                Assert(!overflow.Succeeded && overflow.Error == StorageMigrationPlanError.Unknown,
                    "迁移计划：大小溢出应受控失败");
            }

            // 7. 空间不足与成功字节公式；根级 WAL/SHM 不计入。
            {
                var noSpace = new SpyPlanSpaceProbe(false);
                StorageMigrationPlanResult insufficient = Create(spaceProbe: noSpace).Plan(parent);
                Assert(!insufficient.Succeeded && insufficient.Error == StorageMigrationPlanError.InsufficientSpace,
                    "迁移计划：空间不足应拒绝");

                StorageMigrationPlanResult success = Create().Plan(parent);
                Assert(success.Succeeded && success.Plan is not null,
                    $"迁移计划：成功基线应通过 detail={success.Detail}");
                Assert(success.Plan!.SourceBytes == 100,
                    $"迁移计划：WAL/SHM 不计入，source 应为 100 bytes，实际={success.Plan.SourceBytes}");
                Assert(success.Plan.RequiredBytes == 100 + 64L * 1024 * 1024,
                    "迁移计划：required 应使用 64 MiB 最小余量");
                Assert(success.Plan.TargetRoot == Path.Combine(parent, "Clipora"),
                    "迁移计划：target 固定为 <parent>\\Clipora");
            }

            // 8. 完整/残缺 pending 均拒绝，不覆盖。
            {
                Guid existingId = Guid.NewGuid();
                var completePending = new MemoryMigrationQueueStore(
                    new StorageMigrationState(source, Path.Combine(root, "ExistingTarget"), existingId, null));
                Assert(Create(completePending).Plan(parent).Error == StorageMigrationPlanError.PendingExists,
                    "迁移计划：完整 pending 应拒绝");
                Assert(completePending.EnqueueCount == 0, "迁移计划：不得覆盖完整 pending");

                var pendingOnly = new MemoryMigrationQueueStore(
                    new StorageMigrationState(source, Path.Combine(root, "PartialTarget"), null, null));
                Assert(Create(pendingOnly).Plan(parent).Error == StorageMigrationPlanError.PendingExists,
                    "迁移计划：仅 PendingRoot 的残缺状态应拒绝");

                var idOnly = new MemoryMigrationQueueStore(
                    new StorageMigrationState(source, null, existingId, null));
                Assert(Create(idOnly).Plan(parent).Error == StorageMigrationPlanError.PendingExists,
                    "迁移计划：仅 MigrationId 的残缺状态应拒绝");
            }

            // 9. 成功 Enqueue：target/id 精确、active 不变、零 target 创建。
            StorageMigrationPlan stablePlan;
            {
                var store = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null));
                var service = Create(store);
                stablePlan = service.Plan(parent).Plan!;
                StorageMigrationEnqueueResult result = service.Enqueue(stablePlan);
                Assert(result.Succeeded && result.MigrationId is Guid id && id != Guid.Empty,
                    $"迁移计划：Enqueue 应返回非空 id detail={result.Detail}");
                Assert(store.State.MigrationId == result.MigrationId
                    && string.Equals(store.State.PendingRoot, stablePlan.TargetRoot, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(store.State.ActiveRoot, source, StringComparison.OrdinalIgnoreCase),
                    "迁移计划：Enqueue 后 pending/id 精确且 ActiveRoot 不变");
                Assert(!Directory.Exists(stablePlan.TargetRoot),
                    "迁移计划：Enqueue 不得创建 target");
            }

            // 10. plan 后 source 大小/target/pending 变化必须阻止入队。
            {
                var sizeStore = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null));
                var sizeService = Create(sizeStore);
                StorageMigrationPlan oldPlan = sizeService.Plan(parent).Plan!;
                string lateFile = Path.Combine(source, "late.bin");
                File.WriteAllText(lateFile, "changed");
                StorageMigrationEnqueueResult changed = sizeService.Enqueue(oldPlan);
                Assert(!changed.Succeeded && changed.Error == StorageMigrationPlanError.PlanChanged,
                    "迁移计划：source 大小变化应 PlanChanged");
                File.Delete(lateFile);

                string raceParent = Path.Combine(root, "RaceTargetParent");
                Directory.CreateDirectory(raceParent);
                var targetStore = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null));
                var targetService = Create(targetStore);
                StorageMigrationPlan targetPlan = targetService.Plan(raceParent).Plan!;
                Directory.CreateDirectory(targetPlan.TargetRoot);
                Assert(targetService.Enqueue(targetPlan).Error == StorageMigrationPlanError.PlanChanged,
                    "迁移计划：确认后 target 出现应 PlanChanged");

                var pendingRaceStore = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null));
                var pendingRaceService = Create(pendingRaceStore);
                StorageMigrationPlan pendingPlan = pendingRaceService.Plan(parent).Plan!;
                pendingRaceStore.State = pendingRaceStore.State with
                {
                    PendingRoot = Path.Combine(root, "RacePending"),
                    MigrationId = Guid.NewGuid(),
                };
                Assert(pendingRaceService.Enqueue(pendingPlan).Error == StorageMigrationPlanError.PendingExists,
                    "迁移计划：确认后出现 pending 应 PendingExists");
            }

            // 11. store 读/写异常与写后撒谎全部受控，不清理未知状态。
            {
                var readFailureStore = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null))
                {
                    ReadException = new IOException("模拟读取失败"),
                };
                Assert(!Create(readFailureStore).Plan(parent).Succeeded,
                    "迁移计划：store.Read 异常应受控失败");

                var enqueueFailureStore = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null));
                var enqueueFailureService = Create(enqueueFailureStore);
                StorageMigrationPlan enqueuePlan = enqueueFailureService.Plan(parent).Plan!;
                enqueueFailureStore.EnqueueException = new IOException("模拟入队失败");
                Assert(enqueueFailureService.Enqueue(enqueuePlan).Error == StorageMigrationPlanError.Unknown,
                    "迁移计划：store.Enqueue 异常应受控失败");

                var lyingStore = new MemoryMigrationQueueStore(new StorageMigrationState(source, null, null, null))
                {
                    IgnoreEnqueue = true,
                };
                var lyingService = Create(lyingStore);
                StorageMigrationPlan lyingPlan = lyingService.Plan(parent).Plan!;
                Assert(lyingService.Enqueue(lyingPlan).Error == StorageMigrationPlanError.Unknown,
                    "迁移计划：写后核验撒谎应受控失败");
                Assert(lyingStore.ClearCount == 0,
                    "迁移计划：写后核验失败不得清理未知状态");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }

            string? prodDataRootAfter = ReadProductionDataRoot();
            bool prodKeyExistedAfter = ProductionStorageKeyExists();
            Assert(prodKeyExistedBefore == prodKeyExistedAfter,
                "迁移计划自检：生产 Storage 键存在性应在前后一致");
            Assert(string.Equals(prodDataRootBefore, prodDataRootAfter, StringComparison.Ordinal),
                "迁移计划自检：生产 DataRoot 应在前后保持不变");
        }
    }

    // ── SettingsViewModel 迁移工作流自检（M4.2.3a-3b） ──────────────────────

    private static void RunStorageMigrationSettingsTests()
    {
        string testDir = Path.Combine(Path.GetTempPath(), "clipora-selftest-data");
        var storageStub = new SelfTestSettingsService();
        var autoStartTest = new AutoStartService("Clipora_SelfTest");

        // 1. 旧三参数构造：迁移不可用，说明为开发版固定目录。
        {
            var svm = new SettingsViewModel(storageStub, autoStartTest, testDir);
            Assert(!svm.CanChangeDataDirectory,
                "旧三参数构造：CanChangeDataDirectory 应为 false");
            Assert(svm.DataDirectoryMigrationDescription == "开发版数据目录固定为 .dev-data",
                $"旧三参数构造：说明应为开发版固定文案（实际={svm.DataDirectoryMigrationDescription}）");
        }

        // 2. 不可用时 planner/restart spy 零调用。
        {
            var planner = new SpyMigrationPlanner();
            int restartCalls = 0;
            var svm = new SettingsViewModel(
                storageStub, autoStartTest, testDir,
                planner, canChangeDataDirectory: false,
                restartDelegate: _ => { restartCalls++; return (true, null); });

            Assert(!svm.CanChangeDataDirectory,
                "不可用构造：CanChangeDataDirectory 应为 false");
            Assert(svm.DataDirectoryMigrationDescription == "开发版数据目录固定为 .dev-data",
                "不可用构造：说明应为开发版固定文案");

            // Plan/Enqueue 仍可调用但 planner 在可用性闸门之前到达
            StorageMigrationPlanResult planResult = svm.TryPlan("C:\\test");
            Assert(!planResult.Succeeded && planResult.Error == StorageMigrationPlanError.Unavailable,
                "不可用构造：TryPlan 应返回 Unavailable ");
            Assert(planner.PlanCount == 0,
                "不可用构造：TryPlan 不应调用 planner（可用性闸门在 SettingsViewModel 层，但 planner=null 时短路）");

            StorageMigrationWorkflowResult wfResult = svm.TryEnqueueAndRestart(
                new StorageMigrationPlan("C:\\src", "C:\\parent", "C:\\parent\\Clipora", 100, 100 + 64L * 1024 * 1024));
            Assert(!wfResult.Succeeded && wfResult.Error == StorageMigrationWorkflowError.EnqueueFailed,
                "不可用构造：TryEnqueueAndRestart 应失败");
            Assert(planner.EnqueueCount == 0,
                "不可用构造：TryEnqueueAndRestart 不应调用 planner.Enqueue");
            Assert(restartCalls == 0,
                "不可用构造：restart delegate 不应被调用");
        }

        // 可用构造基础属性。
        {
            var planner = new SpyMigrationPlanner();
            var svm = new SettingsViewModel(
                storageStub, autoStartTest, testDir,
                planner, canChangeDataDirectory: true,
                restartDelegate: null);

            Assert(svm.CanChangeDataDirectory,
                "可用构造：CanChangeDataDirectory 应为 true");
            Assert(svm.DataDirectoryMigrationDescription == "选择新的本地父目录，Clipora 将在重启后安全迁移",
                "可用构造：说明应为迁移文案");
        }

        // 3. Plan 失败透传错误且零 Enqueue/restart。
        {
            string sourceRoot = Path.Combine(testDir, "plan-fail-src");
            Directory.CreateDirectory(sourceRoot);
            var planner = new SpyMigrationPlanner
            {
                PlanResultOverride = new StorageMigrationPlanResult(
                    false, null, StorageMigrationPlanError.InvalidParent, "测试 Plan 失败"),
            };
            int restartCalls = 0;
            var svm = new SettingsViewModel(
                storageStub, autoStartTest, testDir,
                planner, canChangeDataDirectory: true,
                restartDelegate: _ => { restartCalls++; return (true, null); });

            StorageMigrationPlanResult result = svm.TryPlan("C:\\invalid");
            Assert(!result.Succeeded && result.Error == StorageMigrationPlanError.InvalidParent,
                "TryPlan 失败：应透传 planner 错误");
            Assert(result.Detail == "测试 Plan 失败",
                "TryPlan 失败：应透传 Detail");
            Assert(planner.PlanCount == 1 && planner.EnqueueCount == 0,
                "TryPlan 失败：应只调用 Plan，不调用 Enqueue");
            Assert(restartCalls == 0,
                "TryPlan 失败：不应调用 restart");
        }

        // 4. Enqueue 失败零 restart。
        {
            string sourceRoot = Path.Combine(testDir, "enq-fail-src");
            Directory.CreateDirectory(sourceRoot);
            var planner = new SpyMigrationPlanner
            {
                PlanResultOverride = new StorageMigrationPlanResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\parent", "C:\\parent\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    StorageMigrationPlanError.None, null),
                EnqueueResultOverride = new StorageMigrationEnqueueResult(
                    false, null, null, StorageMigrationPlanError.PlanChanged, "测试 Enqueue 失败"),
            };
            int restartCalls = 0;
            var svm = new SettingsViewModel(
                storageStub, autoStartTest, testDir,
                planner, canChangeDataDirectory: true,
                restartDelegate: _ => { restartCalls++; return (true, null); });

            StorageMigrationPlanResult planOk = svm.TryPlan("C:\\parent");
            Assert(planOk.Succeeded, "Enqueue 失败回归：Plan 应先成功");

            StorageMigrationWorkflowResult wf = svm.TryEnqueueAndRestart(planOk.Plan!);
            Assert(!wf.Succeeded && wf.Error == StorageMigrationWorkflowError.EnqueueFailed,
                "Enqueue 失败：应返回 EnqueueFailed");
            Assert(planner.EnqueueCount == 1,
                "Enqueue 失败：应调用 Enqueue 一次");
            Assert(restartCalls == 0,
                "Enqueue 失败：不应调用 restart");
            // 按钮状态：Enqueue 失败不会改变 CanChangeDataDirectory
            Assert(svm.CanChangeDataDirectory,
                "Enqueue 失败：CanChangeDataDirectory 不应改变");
        }

        // 5. Enqueue 成功 → restart 成功：调用顺序严格为 Plan → Enqueue → restart，id 精确传递。
        {
            string sourceRoot = Path.Combine(testDir, "success-src");
            Directory.CreateDirectory(sourceRoot);
            Guid enqueuedId = Guid.NewGuid();
            var planner = new SpyMigrationPlanner
            {
                PlanResultOverride = new StorageMigrationPlanResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\parent", "C:\\parent\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    StorageMigrationPlanError.None, null),
                EnqueueResultOverride = new StorageMigrationEnqueueResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\parent", "C:\\parent\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    enqueuedId,
                    StorageMigrationPlanError.None, null),
            };
            Guid? restartReceivedId = null;
            int restartCalls = 0;
            var svm = new SettingsViewModel(
                storageStub, autoStartTest, testDir,
                planner, canChangeDataDirectory: true,
                restartDelegate: id => { restartCalls++; restartReceivedId = id; return (true, null); });

            bool canChangeNotified = false;
            bool descriptionNotified = false;
            svm.PropertyChanged += (_, args) =>
            {
                canChangeNotified |= args.PropertyName == nameof(SettingsViewModel.CanChangeDataDirectory);
                descriptionNotified |= args.PropertyName == nameof(SettingsViewModel.DataDirectoryMigrationDescription);
            };

            // Plan
            StorageMigrationPlanResult planOk = svm.TryPlan("C:\\parent");
            Assert(planOk.Succeeded, "成功路径：Plan 应先成功");
            Assert(planner.PlanCount == 1, "成功路径：Plan 调用计数 1");

            // Enqueue + restart
            StorageMigrationWorkflowResult wf = svm.TryEnqueueAndRestart(planOk.Plan!);
            Assert(wf.Succeeded && wf.Error == StorageMigrationWorkflowError.None,
                "成功路径：TryEnqueueAndRestart 应成功");
            Assert(planner.EnqueueCount == 1,
                "成功路径：Enqueue 调用计数 1");
            Assert(restartCalls == 1,
                "成功路径：restart delegate 应调用一次");
            Assert(restartReceivedId == enqueuedId,
                $"成功路径：restart delegate 应收到 Enqueue 返回的 MigrationId（期望={enqueuedId}，实际={restartReceivedId}）");
            Assert(planner.PlanCount == 1,
                "成功路径：调用顺序 Plan(=1) → Enqueue(=1) → restart(=1)");

            // 按钮状态
            Assert(!svm.CanChangeDataDirectory,
                "成功入队后：CanChangeDataDirectory 应为 false");
            Assert(svm.DataDirectoryMigrationDescription == "迁移已排队，正在重启…",
                $"成功入队后：说明应为 pending 文案（实际={svm.DataDirectoryMigrationDescription}）");
            Assert(canChangeNotified && descriptionNotified,
                "成功入队后：按钮可用性与说明文案都应发送 PropertyChanged");
        }

        // 6. restart 成功与失败结果可区分。
        {
            string sourceRoot = Path.Combine(testDir, "restart-fail-src");
            Directory.CreateDirectory(sourceRoot);
            Guid enqueuedId = Guid.NewGuid();
            var planner = new SpyMigrationPlanner
            {
                PlanResultOverride = new StorageMigrationPlanResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\parent2", "C:\\parent2\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    StorageMigrationPlanError.None, null),
                EnqueueResultOverride = new StorageMigrationEnqueueResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\parent2", "C:\\parent2\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    enqueuedId,
                    StorageMigrationPlanError.None, null),
            };

            // 6a. restart 成功
            {
                var plannerA = new SpyMigrationPlanner
                {
                    PlanResultOverride = planner.PlanResultOverride,
                    EnqueueResultOverride = planner.EnqueueResultOverride,
                };
                bool restartCalled = false;
                var svmA = new SettingsViewModel(
                    storageStub, autoStartTest, testDir,
                    plannerA, canChangeDataDirectory: true,
                    restartDelegate: _ => { restartCalled = true; return (true, null); });

                StorageMigrationPlanResult planA = svmA.TryPlan("C:\\parent2");
                StorageMigrationWorkflowResult wfA = svmA.TryEnqueueAndRestart(planA.Plan!);
                Assert(wfA.Succeeded && wfA.Error == StorageMigrationWorkflowError.None,
                    "restart 成功：结果应为成功 + None");
                Assert(restartCalled,
                    "restart 成功：delegate 应被调用");
            }

            // 6b. restart 失败
            {
                var plannerB = new SpyMigrationPlanner
                {
                    PlanResultOverride = planner.PlanResultOverride,
                    EnqueueResultOverride = planner.EnqueueResultOverride,
                };
                bool restartCalled = false;
                var svmB = new SettingsViewModel(
                    storageStub, autoStartTest, testDir,
                    plannerB, canChangeDataDirectory: true,
                    restartDelegate: _ => { restartCalled = true; return (false, "测试协调器拒绝"); });

                StorageMigrationPlanResult planB = svmB.TryPlan("C:\\parent2");
                StorageMigrationWorkflowResult wfB = svmB.TryEnqueueAndRestart(planB.Plan!);
                Assert(!wfB.Succeeded && wfB.Error == StorageMigrationWorkflowError.RestartFailed,
                    "restart 失败：结果应为失败 + RestartFailed");
                Assert(wfB.Detail is not null && wfB.Detail.Contains("测试协调器拒绝", StringComparison.Ordinal),
                    "restart 失败：应保留协调器返回的受控错误详情");
                Assert(restartCalled,
                    "restart 失败：delegate 应被调用");
                Assert(!svmB.CanChangeDataDirectory,
                    "restart 失败：按钮应保持禁用（pending 保留）");
                Assert(svmB.DataDirectoryMigrationDescription!.Contains("已排队", StringComparison.Ordinal),
                    "restart 失败：说明应包含 pending 保留文案");
                Assert(plannerB.EnqueueCount == 1,
                    "restart 失败：Enqueue 只调用一次");
                // pending 不由 VM 清理（无法验证注册表，但 spy planner 的 Enqueue 已执行）
            }

            // 6c. restart delegate 缺失不能伪装成成功。
            {
                var plannerC = new SpyMigrationPlanner
                {
                    PlanResultOverride = planner.PlanResultOverride,
                    EnqueueResultOverride = planner.EnqueueResultOverride,
                };
                var svmC = new SettingsViewModel(
                    storageStub, autoStartTest, testDir,
                    plannerC, canChangeDataDirectory: true,
                    restartDelegate: null);

                StorageMigrationPlanResult planC = svmC.TryPlan("C:\\parent2");
                StorageMigrationWorkflowResult wfC = svmC.TryEnqueueAndRestart(planC.Plan!);
                Assert(!wfC.Succeeded && wfC.Error == StorageMigrationWorkflowError.RestartFailed,
                    "restart delegate 缺失：入队后必须返回 RestartFailed，不能伪成功");
                Assert(wfC.Detail is not null && wfC.Detail.Contains("重启服务不可用", StringComparison.Ordinal),
                    "restart delegate 缺失：应返回可诊断的受控错误");
                Assert(!svmC.CanChangeDataDirectory,
                    "restart delegate 缺失：pending 已写入后按钮必须保持禁用");
            }
        }

        // 7. 重复调用不产生第二次 Enqueue/restart。
        {
            string sourceRoot = Path.Combine(testDir, "dup-src");
            Directory.CreateDirectory(sourceRoot);
            Guid enqueuedId = Guid.NewGuid();
            var planner = new SpyMigrationPlanner
            {
                PlanResultOverride = new StorageMigrationPlanResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\dup", "C:\\dup\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    StorageMigrationPlanError.None, null),
                EnqueueResultOverride = new StorageMigrationEnqueueResult(
                    true,
                    new StorageMigrationPlan(sourceRoot, "C:\\dup", "C:\\dup\\Clipora", 100, 100 + 64L * 1024 * 1024),
                    enqueuedId,
                    StorageMigrationPlanError.None, null),
            };
            int restartCalls = 0;
            var svm = new SettingsViewModel(
                storageStub, autoStartTest, testDir,
                planner, canChangeDataDirectory: true,
                restartDelegate: _ => { restartCalls++; return (true, null); });

            StorageMigrationPlanResult plan = svm.TryPlan("C:\\dup");
            Assert(plan.Succeeded, "重复调用回归：Plan 应先成功");

            // 第一次 Enqueue+restart
            StorageMigrationWorkflowResult wf1 = svm.TryEnqueueAndRestart(plan.Plan!);
            Assert(wf1.Succeeded, "第一次 TryEnqueueAndRestart 应成功");
            Assert(planner.EnqueueCount == 1 && restartCalls == 1,
                "第一次：Enqueue + restart 各一次");

            // 第二次 Enqueue+restart：按钮已禁用（CanChangeDataDirectory=false），
            // VM 层闸门在入口即拒绝，不调用 planner/spy。
            planner.EnqueueResultOverride = new StorageMigrationEnqueueResult(
                false, null, null, StorageMigrationPlanError.PendingExists, "已有 pending");
            StorageMigrationWorkflowResult wf2 = svm.TryEnqueueAndRestart(plan.Plan!);
            Assert(!wf2.Succeeded && wf2.Error == StorageMigrationWorkflowError.EnqueueFailed,
                "第二次 TryEnqueueAndRestart：VM 层闸门应拒绝");
            Assert(planner.EnqueueCount == 1,
                "第二次：Enqueue 不应再次调用（VM CanChangeDataDirectory=false 闸门阻止）");
            Assert(restartCalls == 1,
                "第二次：restart 仍只调用一次（不触发重复重启）");
        }

        // 清理
        try { Directory.Delete(testDir, true); } catch { /* ignore */ }
    }

    // ── StorageMigrationProgressPresenter 自检（M4.2.3a-3c-1） ────────────

    private static void RunStorageMigrationProgressPresenterTests()
    {
        // 覆盖全部阶段映射
        Assert(MapPhase(StorageMigrationPhase.Validating).StageText == "正在准备",
            "Validating → 正在准备");
        Assert(MapPhase(StorageMigrationPhase.Checkpointing).StageText == "正在准备",
            "Checkpointing → 正在准备");
        Assert(MapPhase(StorageMigrationPhase.Copying).StageText == "正在复制",
            "Copying → 正在复制");
        Assert(MapPhase(StorageMigrationPhase.Rebasing).StageText == "正在校验",
            "Rebasing → 正在校验");
        Assert(MapPhase(StorageMigrationPhase.Verifying).StageText == "正在校验",
            "Verifying → 正在校验");
        Assert(MapPhase(StorageMigrationPhase.Promoting).StageText == "正在切换",
            "Promoting → 正在切换");
        Assert(MapPhase(StorageMigrationPhase.Switching).StageText == "正在切换",
            "Switching → 正在切换");
        Assert(MapPhase(StorageMigrationPhase.Completed).StageText == "正在切换",
            "Completed → 正在切换");
        Assert(MapPhase(StorageMigrationPhase.Failed).StageText == "迁移失败",
            "Failed → 迁移失败");

        // 只有 Copying + 正数总量 → determinate
        Assert(!MapPhase(StorageMigrationPhase.Validating).IsDeterminate,
            "Validating 不是 determinate");
        Assert(!MapPhase(StorageMigrationPhase.Failed).IsDeterminate,
            "Failed 不是 determinate");
        Assert(!MapPhase(StorageMigrationPhase.Copying).IsDeterminate,
            "Copying + 零总量不是 determinate");

        // Copying + bytes 优先级
        var bytesCopying = new StorageMigrationProgress(
            StorageMigrationPhase.Copying, 3, 10, 50, 200);
        var bytesResult = StorageMigrationProgressPresenter.Map(bytesCopying);
        Assert(bytesResult.IsDeterminate, "Copying + TotalBytes>0 → IsDeterminate");
        Assert(bytesResult.Fraction == 0.25,
            $"bytes Fraction=50/200=0.25（实际={bytesResult.Fraction}）");

        // Copying + bytes 回退 files（TotalBytes<=0）
        var filesCopying = new StorageMigrationProgress(
            StorageMigrationPhase.Copying, 3, 10, 50, 0);
        var filesResult = StorageMigrationProgressPresenter.Map(filesCopying);
        Assert(filesResult.IsDeterminate, "Copying + TotalFiles>0 → IsDeterminate（bytes 回退）");
        Assert(filesResult.Fraction == 0.3,
            $"files Fraction=3/10=0.3（实际={filesResult.Fraction}）");

        // Copying + TotalFiles<=0 且 TotalBytes<=0 → indeterminate, fraction=0
        var zeroBoth = new StorageMigrationProgress(
            StorageMigrationPhase.Copying, 0, 0, 0, 0);
        var zeroResult = StorageMigrationProgressPresenter.Map(zeroBoth);
        Assert(!zeroResult.IsDeterminate, "Copying + 零总量 → IsDeterminate=false");
        Assert(zeroResult.Fraction == 0, "Copying + 零总量 → Fraction=0");

        // bytes 超 1 → clamp
        var overBytes = new StorageMigrationProgress(
            StorageMigrationPhase.Copying, 0, 0, 200, 100);
        var overResult = StorageMigrationProgressPresenter.Map(overBytes);
        Assert(overResult.Fraction == 1,
            $"CompletedBytes > TotalBytes → clamp 1（实际={overResult.Fraction}）");

        // 负数 → clamp 0
        var negBytes = new StorageMigrationProgress(
            StorageMigrationPhase.Copying, 0, 0, -10, 100);
        var negResult = StorageMigrationProgressPresenter.Map(negBytes);
        Assert(negResult.Fraction == 0,
            $"负数 CompletedBytes → clamp 0（实际={negResult.Fraction}）");

        // files 超 1 → clamp
        var overFiles = new StorageMigrationProgress(
            StorageMigrationPhase.Copying, 15, 10, 0, 0);
        var overFilesResult = StorageMigrationProgressPresenter.Map(overFiles);
        Assert(overFilesResult.Fraction == 1,
            $"CompletedFiles > TotalFiles → clamp 1（实际={overFilesResult.Fraction}）");

        // 非 Copying 阶段忽略 bytes/files，Fraction=0
        var nonCopying = new StorageMigrationProgress(
            StorageMigrationPhase.Verifying, 5, 10, 500, 1000);
        var nonResult = StorageMigrationProgressPresenter.Map(nonCopying);
        Assert(!nonResult.IsDeterminate, "Verifying 不是 determinate");
        Assert(nonResult.Fraction == 0, "Verifying Fraction=0（忽略进度值）");
    }

    private static void RunStorageStartupWindowPolicyTests()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), "clipora-selftest-policy-missing");
        var missing = new StorageLocationException(
            StorageLocationError.MissingDirectory,
            "missing",
            missingPath);
        Assert(StorageStartupWindowPolicy.CanUseDefault(missing),
            "MissingDirectory + 结构化路径应允许显式默认恢复");
        Assert(!StorageStartupWindowPolicy.CanUseDefault(new StorageLocationException(
                StorageLocationError.MissingDirectory,
                "missing")),
            "MissingDirectory 缺结构化路径时不得显示默认恢复");
        Assert(!StorageStartupWindowPolicy.CanUseDefault(new StorageLocationException(
                StorageLocationError.AccessDenied,
                "denied",
                missingPath)),
            "非 MissingDirectory 不得显示默认恢复");

        var none = new StorageMigrationStartupResult(
            StorageMigrationStartupAction.None,
            missingPath,
            StorageMigrationError.None,
            null);
        var completed = new StorageMigrationStartupResult(
            StorageMigrationStartupAction.Completed,
            missingPath,
            StorageMigrationError.None,
            null,
            "C:\\source",
            "C:\\target");
        var failed = new StorageMigrationStartupResult(
            StorageMigrationStartupAction.Failed,
            missingPath,
            StorageMigrationError.CopyFailed,
            "failed");

        Assert(StorageStartupWindowPolicy.ShouldContinue(none)
            && StorageStartupWindowPolicy.ShouldContinue(completed)
            && !StorageStartupWindowPolicy.ShouldContinue(failed),
            "启动窗口只应在 None/Completed 后继续组合根");
        Assert(!StorageStartupWindowPolicy.ShouldForceShowMain(false, none),
            "普通无迁移启动不应强制显示主面板");
        Assert(StorageStartupWindowPolicy.ShouldForceShowMain(true, none),
            "completion 参数启动应强制显示主面板");
        Assert(StorageStartupWindowPolicy.ShouldForceShowMain(false, completed),
            "本次 Completed 应强制显示主面板");
        Assert(!StorageStartupWindowPolicy.ShouldForceShowMain(false, failed),
            "Failed 不得伪装成功并强制显示主面板");
    }

    private static StorageMigrationProgressPresentation MapPhase(StorageMigrationPhase phase) =>
        StorageMigrationProgressPresenter.Map(
            new StorageMigrationProgress(phase, 0, 0, 0, 0));

    // ── StorageDefaultRecoveryService 自检（M4.2.3a-3c-1） ──────────────────

    private static void RunStorageDefaultRecoveryTests()
    {
        string? prodDataRootBefore = ReadProductionDataRoot();
        bool prodKeyExistedBefore = ProductionStorageKeyExists();

        var tempDirs = new System.Collections.Generic.List<string>();
        var testKeySuffixes = new System.Collections.Generic.List<string>();

        try
        {
            // 1. null/空白/相对/非法 expected
            RunRecoveryTest_InvalidExpected(testKeySuffixes);

            // 2. key/DataRoot 不存在、DataRoot 类型错误、expected 与当前值不一致
            RunRecoveryTest_DataRootMismatch(testKeySuffixes);

            // 3. PendingRoot-only、MigrationId-only、两者完整、空白值、错误类型均拒绝且零修改
            RunRecoveryTest_PendingRejection(testKeySuffixes);

            // 4. expected 等于 default 拒绝
            RunRecoveryTest_AlreadyDefault(testKeySuffixes);

            // 5. 成功只删除 DataRoot/LastSourceRoot，保留无关 sentinel
            RunRecoveryTest_SuccessOnlyDeletesTwoValues(testKeySuffixes, tempDirs);

            // 6. 成功不创建 default、不删除 expected 指向目录或其中 sentinel 文件
            RunRecoveryTest_SuccessNoSideEffects(testKeySuffixes, tempDirs);

            // 7. 删除后复验失败 → Unknown（注入 spy）
            RunRecoveryTest_VerifyFailure(testKeySuffixes);

            // 8. 底层异常映射
            RunRecoveryTest_ExceptionMapping(testKeySuffixes);
        }
        finally
        {
            foreach (string d in tempDirs)
                try { Directory.Delete(d, true); } catch { /* ignore */ }

            foreach (string suffix in testKeySuffixes)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        @"Software\Clipora_SelfTest_DefaultRecovery_" + suffix,
                        throwOnMissingSubKey: false);
                }
                catch { /* best-effort */ }
            }

            string? prodDataRootAfter = ReadProductionDataRoot();
            bool prodKeyExistedAfter = ProductionStorageKeyExists();
            Assert(prodKeyExistedBefore == prodKeyExistedAfter,
                "恢复服务自检：生产 Storage 键存在性应在前后一致");
            Assert(string.Equals(prodDataRootBefore, prodDataRootAfter, StringComparison.Ordinal),
                $"恢复服务自检：生产 DataRoot 应不变（before={prodDataRootBefore ?? "(null)"}，after={prodDataRootAfter ?? "(null)"}）");
        }
    }

    private static string CreateRecoveryTestKey(System.Collections.Generic.List<string> suffixes)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        suffixes.Add(suffix);
        return @"Software\Clipora_SelfTest_DefaultRecovery_" + suffix;
    }

    private static string CreateRecoveryTestDir(System.Collections.Generic.List<string> dirs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "clipora-selftest-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dirs.Add(dir);
        return dir;
    }

    /// <summary>生成合法临时路径但不创建目录（用于 defaultRoot 等不需要真实目录的测试参数）。</summary>
    private static string MakeRecoveryTestPath(string prefix = "clipora-selftest-recovery-") =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    // ── 个案 ────────────────────────────────────────────────────────

    private static void RunRecoveryTest_InvalidExpected(System.Collections.Generic.List<string> suffixes)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath();
        var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);

        foreach (string? invalid in new[] { null, "", "relative", "C:relative" })
        {
            var result = service.RecoverMissingRoot(invalid!);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.InvalidExpectedRoot,
                $"非法 expected 应拒绝 ({invalid ?? "null"})");
        }
    }

    private static void RunRecoveryTest_DataRootMismatch(System.Collections.Generic.List<string> suffixes)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath();
        string expected = Path.Combine(Path.GetTempPath(), "clipora-selftest-expected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(expected);

        // key 不存在 → StateChanged
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.StateChanged,
                "key 不存在 → StateChanged");
        }

        // DataRoot 不存在 → StateChanged
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) { /* empty key */ }
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.StateChanged,
                "DataRoot 不存在 → StateChanged");
        }

        // DataRoot 类型错误（REG_DWORD）→ StateChanged
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, 42, RegistryValueKind.DWord);
        }
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.StateChanged,
                "DataRoot 类型错误 → StateChanged");
        }

        // DataRoot 值与 expected 不一致 → StateChanged
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        string otherPath = Path.Combine(Path.GetTempPath(), "clipora-selftest-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherPath);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, otherPath, RegistryValueKind.String);
        }
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.StateChanged,
                "DataRoot 与 expected 不一致 → StateChanged");
        }

        try { Directory.Delete(expected, true); } catch { }
        try { Directory.Delete(otherPath, true); } catch { }
    }

    private static void RunRecoveryTest_PendingRejection(System.Collections.Generic.List<string> suffixes)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath();
        string expected = Path.Combine(Path.GetTempPath(), "clipora-selftest-pending-expected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(expected);

        // 设置合法 DataRoot
        void SetupDataRoot()
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, expected, RegistryValueKind.String);
        }

        // PendingRoot-only
        SetupDataRoot();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            key!.SetValue(StorageRegistryKeys.ValueNamePendingRoot, "C:\\somewhere", RegistryValueKind.String);
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.PendingExists,
                "PendingRoot 存在 → PendingExists");
        }

        // MigrationId-only
        SetupDataRoot();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            key!.SetValue(StorageRegistryKeys.ValueNameMigrationId, Guid.NewGuid().ToString("D"), RegistryValueKind.String);
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.PendingExists,
                "MigrationId 存在 → PendingExists");
        }

        // 两者完整
        SetupDataRoot();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
        {
            key!.SetValue(StorageRegistryKeys.ValueNamePendingRoot, "C:\\somewhere", RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, Guid.NewGuid().ToString("D"), RegistryValueKind.String);
        }
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.PendingExists,
                "PendingRoot + MigrationId 完整 → PendingExists");
        }

        // 空白值（PendingRoot=""）→ 仍视为存在，拒绝
        SetupDataRoot();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            key!.SetValue(StorageRegistryKeys.ValueNamePendingRoot, "", RegistryValueKind.String);
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.PendingExists,
                "PendingRoot=空白字符串 → 仍应 PendingExists");
        }

        // 错误类型（PendingRoot=REG_DWORD）→ 仍视为存在，拒绝
        SetupDataRoot();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            key!.SetValue(StorageRegistryKeys.ValueNamePendingRoot, 1, RegistryValueKind.DWord);
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            var result = service.RecoverMissingRoot(expected);
            Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.PendingExists,
                "PendingRoot=REG_DWORD → 仍应 PendingExists（不依赖 Guid.TryParse）");
        }

        // 零修改验证：拒绝后 DataRoot 仍存在
        SetupDataRoot();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            key!.SetValue(StorageRegistryKeys.ValueNamePendingRoot, "C:\\p", RegistryValueKind.String);
        {
            var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
            service.RecoverMissingRoot(expected);
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            Assert(key!.GetValue(StorageRegistryKeys.ValueNameDataRoot) as string is not null,
                "Pending 拒绝后 DataRoot 应保留（零修改）");
        }

        try { Directory.Delete(expected, true); } catch { }
    }

    private static void RunRecoveryTest_AlreadyDefault(System.Collections.Generic.List<string> suffixes)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath();

        // expected == default → AlreadyDefault
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, defaultRoot, RegistryValueKind.String);
        }
        var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
        var result = service.RecoverMissingRoot(defaultRoot);
        Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.AlreadyDefault,
            "expected=default → AlreadyDefault");
    }

    private static void RunRecoveryTest_SuccessOnlyDeletesTwoValues(
        System.Collections.Generic.List<string> suffixes,
        System.Collections.Generic.List<string> tempDirs)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = CreateRecoveryTestDir(tempDirs);
        string expected = CreateRecoveryTestDir(tempDirs);

        // 设置 DataRoot + LastSourceRoot + 无关 sentinel
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, expected, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameLastSourceRoot, Path.Combine(Path.GetTempPath(), "old-source"), RegistryValueKind.String);
            key.SetValue("SomeUnrelatedSetting", "keep-me", RegistryValueKind.String);
        }

        var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
        var result = service.RecoverMissingRoot(expected);
        Assert(result.Succeeded,
            $"成功删除 DataRoot+LastSourceRoot 应返回成功 detail={result.Detail}");
        Assert(string.Equals(result.DefaultRoot, Path.GetFullPath(defaultRoot), StringComparison.OrdinalIgnoreCase),
            "成功结果应返回 defaultRoot");

        // 复验：DataRoot 已删除
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
        {
            Assert(key is not null, "成功删除后 key 应仍存在");
            Assert(!Array.Exists(key!.GetValueNames(),
                n => string.Equals(n, StorageRegistryKeys.ValueNameDataRoot, StringComparison.OrdinalIgnoreCase)),
                "成功后 DataRoot 应不存在");
            Assert(!Array.Exists(key.GetValueNames(),
                n => string.Equals(n, StorageRegistryKeys.ValueNameLastSourceRoot, StringComparison.OrdinalIgnoreCase)),
                "成功后 LastSourceRoot 应不存在");
            Assert(Array.Exists(key.GetValueNames(),
                n => string.Equals(n, "SomeUnrelatedSetting", StringComparison.OrdinalIgnoreCase)),
                "成功后无关 sentinel 应保留");
            // PendingRoot/MigrationId 不得被删除（本来就没有，但验证不会意外删除）
        }
    }

    private static void RunRecoveryTest_SuccessNoSideEffects(
        System.Collections.Generic.List<string> suffixes,
        System.Collections.Generic.List<string> tempDirs)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath("clipora-selftest-recovery-default-missing-");
        string expected = CreateRecoveryTestDir(tempDirs);
        Assert(!Directory.Exists(defaultRoot), "恢复测试前 default 目录不应存在");

        // 在 expected 目录放 sentinel 文件
        string sentinelFile = Path.Combine(expected, "some-data.txt");
        File.WriteAllText(sentinelFile, "keep me");

        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, expected, RegistryValueKind.String);
        }

        var service = new StorageDefaultRecoveryService(keyPath, defaultRoot);
        var result = service.RecoverMissingRoot(expected);
        Assert(result.Succeeded,
            $"成功恢复不应创建/删除目录或文件 detail={result.Detail}");

        Assert(!Directory.Exists(defaultRoot), "成功恢复不得创建 default 目录");

        // 不删除 expected 目录或其中文件
        Assert(Directory.Exists(expected), "成功恢复不得删除 expected 目录");
        Assert(File.Exists(sentinelFile), "成功恢复不得删除 expected 目录中的 sentinel 文件");
    }

    private static void RunRecoveryTest_VerifyFailure(System.Collections.Generic.List<string> suffixes)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath();
        string expected = Path.Combine(Path.GetTempPath(), "clipora-selftest-verify-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(expected);

        // 设置 DataRoot + LastSourceRoot
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, expected, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameLastSourceRoot, Path.Combine(Path.GetTempPath(), "old-src"), RegistryValueKind.String);
        }

        // 注入 spy registry：删除成功但复验时谎报值仍在
        var spyKey = new SpyRecoveryRegistryKey(keyPath, writable: true);
        spyKey.LieAboutDataRootAfterDelete = true;
        var service = new StorageDefaultRecoveryService(keyPath, defaultRoot, _ => spyKey);

        var result = service.RecoverMissingRoot(expected);
        Assert(!result.Succeeded && result.Error == StorageDefaultRecoveryError.Unknown,
            $"复验失败 → Unknown detail={result.Detail}");

        spyKey.Dispose();

        // 清理
        try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(expected, true); } catch { }
    }

    private static void RunRecoveryTest_ExceptionMapping(System.Collections.Generic.List<string> suffixes)
    {
        string keyPath = CreateRecoveryTestKey(suffixes);
        string defaultRoot = MakeRecoveryTestPath();

        // 权限异常 → AccessDenied
        var accessDeniedKey = new ThrowingRecoveryRegistryKey(new UnauthorizedAccessException("模拟权限错误"));
        var service1 = new StorageDefaultRecoveryService(keyPath, defaultRoot, _ => accessDeniedKey);
        var result1 = service1.RecoverMissingRoot("C:\\valid-expected");
        Assert(!result1.Succeeded && result1.Error == StorageDefaultRecoveryError.AccessDenied,
            $"UnauthorizedAccessException → AccessDenied（实际={result1.Error}）");

        // SecurityException → AccessDenied
        var securityKey = new ThrowingRecoveryRegistryKey(new SecurityException("模拟安全异常"));
        var serviceSecurity = new StorageDefaultRecoveryService(keyPath, defaultRoot, _ => securityKey);
        var resultSecurity = serviceSecurity.RecoverMissingRoot("C:\\valid-expected");
        Assert(!resultSecurity.Succeeded && resultSecurity.Error == StorageDefaultRecoveryError.AccessDenied,
            $"SecurityException → AccessDenied（实际={resultSecurity.Error}）");

        // IO/其他异常 → Unknown
        var ioKey = new ThrowingRecoveryRegistryKey(new IOException("模拟IO异常"));
        var service2 = new StorageDefaultRecoveryService(keyPath, defaultRoot, _ => ioKey);
        var result2 = service2.RecoverMissingRoot("C:\\valid-expected");
        Assert(!result2.Succeeded && result2.Error == StorageDefaultRecoveryError.Unknown,
            $"IOException → Unknown（实际={result2.Error}）");

        string expected = MakeRecoveryTestPath("clipora-selftest-recovery-exception-");

        // GetValueKind 权限异常不能被适配器吞成 StateChanged。
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, expected, RegistryValueKind.String);
        var kindSpy = new SpyRecoveryRegistryKey(keyPath, writable: true)
        {
            GetValueKindException = new UnauthorizedAccessException("模拟类型读取权限错误"),
        };
        var kindService = new StorageDefaultRecoveryService(keyPath, defaultRoot, _ => kindSpy);
        StorageDefaultRecoveryResult kindResult = kindService.RecoverMissingRoot(expected);
        Assert(!kindResult.Succeeded && kindResult.Error == StorageDefaultRecoveryError.AccessDenied,
            "GetValueKind 权限异常应映射为 AccessDenied，不能降级为 StateChanged");

        // LastSourceRoot 删除权限异常不能被吞并成普通复验失败。
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, expected, RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNameLastSourceRoot, "C:\\old", RegistryValueKind.String);
        }
        var deleteSpy = new SpyRecoveryRegistryKey(keyPath, writable: true)
        {
            LastSourceDeleteException = new UnauthorizedAccessException("模拟删除权限错误"),
        };
        var deleteService = new StorageDefaultRecoveryService(keyPath, defaultRoot, _ => deleteSpy);
        StorageDefaultRecoveryResult deleteResult = deleteService.RecoverMissingRoot(expected);
        Assert(!deleteResult.Succeeded && deleteResult.Error == StorageDefaultRecoveryError.AccessDenied,
            "LastSourceRoot 删除权限异常应映射为 AccessDenied，不能被吞并");
    }

    // ── 恢复服务自检用 spy/桩 ────────────────────────────────────────

    private sealed class SpyRecoveryRegistryKey : IRecoveryRegistryKey
    {
        private readonly RegistryKey _inner;
        public bool LieAboutDataRootAfterDelete { get; set; }
        public Exception? GetValueKindException { get; set; }
        public Exception? LastSourceDeleteException { get; set; }
        private bool _dataRootDeleted;

        public SpyRecoveryRegistryKey(string keyPath, bool writable)
        {
            _inner = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException("test key must exist");
        }

        public string? GetStringValue(string valueName) => _inner.GetValue(valueName) as string;

        public RegistryValueKind? GetValueKind(string valueName)
        {
            if (GetValueKindException is not null)
                throw GetValueKindException;
            try { return _inner.GetValueKind(valueName); }
            catch { return null; }
        }

        public string[] GetValueNames() => _inner.GetValueNames();

        public void SetStringValue(string valueName, string value) =>
            _inner.SetValue(valueName, value, RegistryValueKind.String);

        public void DeleteValue(string valueName)
        {
            if (LastSourceDeleteException is not null
                && string.Equals(valueName, StorageRegistryKeys.ValueNameLastSourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw LastSourceDeleteException;
            }
            _inner.DeleteValue(valueName, throwOnMissingValue: false);
            if (string.Equals(valueName, StorageRegistryKeys.ValueNameDataRoot, StringComparison.OrdinalIgnoreCase))
                _dataRootDeleted = true;
        }

        public bool ValueExists(string valueName)
        {
            if (LieAboutDataRootAfterDelete && _dataRootDeleted
                && string.Equals(valueName, StorageRegistryKeys.ValueNameDataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true; // 谎报：明明删了却说还在
            }

            foreach (string name in _inner.GetValueNames())
                if (string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class ThrowingRecoveryRegistryKey : IRecoveryRegistryKey
    {
        private readonly Exception _ex;

        public ThrowingRecoveryRegistryKey(Exception? ex = null) =>
            _ex = ex ?? new UnauthorizedAccessException("模拟注册表权限错误");

        public string? GetStringValue(string valueName) => throw _ex;
        public RegistryValueKind? GetValueKind(string valueName) => throw _ex;
        public string[] GetValueNames() => throw _ex;
        public void SetStringValue(string valueName, string value) => throw _ex;
        public void DeleteValue(string valueName) => throw _ex;
        public bool ValueExists(string valueName) => throw _ex;
        public void Dispose() { }
    }

    private sealed class SpyMigrationPlanner : IStorageMigrationPlanner
    {
        public int PlanCount { get; private set; }
        public int EnqueueCount { get; private set; }
        public StorageMigrationPlanResult? PlanResultOverride { get; set; }
        public StorageMigrationEnqueueResult? EnqueueResultOverride { get; set; }

        public StorageMigrationPlanResult Plan(string? selectedParent)
        {
            PlanCount++;
            return PlanResultOverride
                ?? new StorageMigrationPlanResult(false, null, StorageMigrationPlanError.Unavailable, "spy default");
        }

        public StorageMigrationEnqueueResult Enqueue(StorageMigrationPlan? plan)
        {
            EnqueueCount++;
            return EnqueueResultOverride
                ?? new StorageMigrationEnqueueResult(false, null, null, StorageMigrationPlanError.Unknown, "spy default");
        }
    }

    private sealed class MemoryMigrationQueueStore : IStorageMigrationQueueStore
    {
        public MemoryMigrationQueueStore(StorageMigrationState state) => State = state;

        public StorageMigrationState State { get; set; }
        public int ReadCount { get; private set; }
        public int EnqueueCount { get; private set; }
        public int ClearCount { get; private set; }
        public Exception? ReadException { get; set; }
        public Exception? EnqueueException { get; set; }
        public bool IgnoreEnqueue { get; set; }

        public StorageMigrationState Read()
        {
            ReadCount++;
            if (ReadException is not null)
                throw ReadException;
            return State;
        }

        public void Enqueue(string targetRoot, Guid migrationId)
        {
            EnqueueCount++;
            if (EnqueueException is not null)
                throw EnqueueException;
            if (!IgnoreEnqueue)
                State = State with { PendingRoot = targetRoot, MigrationId = migrationId };
        }
    }

    private sealed class SpyPlanSpaceProbe : ISpaceProbe
    {
        private readonly bool _result;
        public SpyPlanSpaceProbe(bool result) => _result = result;
        public int CallCount { get; private set; }
        public bool HasSufficientSpace(string targetParent, long requiredBytes)
        {
            CallCount++;
            return _result;
        }
    }

    private sealed class SpyPlanFileSystem : IStorageMigrationPlanFileSystem
    {
        private readonly StorageMigrationPlanFileSystem _inner = new();

        public int CallCount { get; private set; }
        public bool ForceNetwork { get; set; }
        public string? ForcedReparsePath { get; set; }
        public bool FailProbeCreation { get; set; }
        public bool FailEnumeration { get; set; }
        public long? FileLengthOverride { get; set; }

        public bool DirectoryExists(string path) { CallCount++; return _inner.DirectoryExists(path); }
        public bool FileExists(string path) { CallCount++; return _inner.FileExists(path); }
        public FileAttributes GetAttributes(string path)
        {
            CallCount++;
            FileAttributes attributes = _inner.GetAttributes(path);
            return string.Equals(Path.GetFullPath(path), ForcedReparsePath is null ? null : Path.GetFullPath(ForcedReparsePath), StringComparison.OrdinalIgnoreCase)
                ? attributes | FileAttributes.ReparsePoint
                : attributes;
        }
        public IEnumerable<string> EnumerateFileSystemEntries(string directory)
        {
            CallCount++;
            if (FailEnumeration)
                throw new IOException("模拟枚举失败");
            return _inner.EnumerateFileSystemEntries(directory);
        }
        public long GetFileLength(string path)
        {
            CallCount++;
            return FileLengthOverride ?? _inner.GetFileLength(path);
        }
        public void CreateProbe(string path)
        {
            CallCount++;
            if (FailProbeCreation)
                throw new UnauthorizedAccessException("模拟写探针失败");
            _inner.CreateProbe(path);
        }
        public void DeleteFile(string path) { CallCount++; _inner.DeleteFile(path); }
        public bool IsNetworkPath(string path) { CallCount++; return ForceNetwork || _inner.IsNetworkPath(path); }
    }

    /// <summary>不接触系统剪贴板，验证各卡片类型的拖出 DataObject 格式。</summary>
    public static int RunDragData()
    {
        string resultPath = Path.Combine(Path.GetTempPath(), "clipora-drag-selftest-result.txt");
        string dir = Path.Combine(Path.GetTempPath(), "clipora-drag-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);

            Assert(ClipDragDataBuilder.TryBuild(
                new ClipItem { Type = ClipType.Text, TextContent = "drag text" },
                out DataObject? textData),
                "文字应可构造拖出数据");
            Assert(textData?.GetData(DataFormats.UnicodeText) as string == "drag text", "文字拖出应包含 UnicodeText");
            Assert(textData?.GetData(DataFormats.Text) as string == "drag text", "文字拖出应包含兼容 Text");
            Assert(textData?.GetDataPresent(DataFormats.FileDrop, autoConvert: false) == false,
                "文字拖出不应被自动转换为 FileDrop");

            string richPath = Path.Combine(dir, "drag.rtf");
            File.WriteAllText(richPath, @"{\rtf1 drag rich}", Encoding.UTF8);
            Assert(ClipDragDataBuilder.TryBuild(
                new ClipItem { Type = ClipType.RichText, TextContent = "drag rich", RefPath = richPath },
                out DataObject? richData),
                "富文本应可构造拖出数据");
            Assert(richData?.GetDataPresent(DataFormats.UnicodeText) == true
                && richData.GetDataPresent(DataFormats.Rtf),
                "富文本拖出应同时包含 UnicodeText 和 RTF");

            string filePath = Path.Combine(dir, "drag-file.txt");
            File.WriteAllText(filePath, "file payload");
            string manifestPath = Path.Combine(dir, "drag-files.json");
            new ClipFileManifest
            {
                Entries =
                {
                    new ClipFileManifestEntry
                    {
                        OriginalPath = filePath,
                        StoredPath = filePath,
                        DisplayName = "drag-file.txt",
                    },
                },
            }.Save(manifestPath);
            Assert(ClipDragDataBuilder.TryBuild(
                new ClipItem { Type = ClipType.File, RefPath = manifestPath },
                out DataObject? fileData),
                "文件应可构造拖出数据");
            Assert(fileData?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
                && files[0] == filePath,
                "文件拖出应包含有效 FileDrop");
            Assert(GetPreferredDropEffect(fileData) == DragDropEffects.Copy,
                "文件拖出应明确声明复制效果");

            string imagePath = Path.Combine(dir, "drag-image.png");
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0x30, 0x70, 0xB0, 0x00 }, 4);
            ClipboardImageNormalizer.SavePng(bitmap, imagePath);
            Assert(ClipDragDataBuilder.TryBuild(
                new ClipItem { Type = ClipType.Image, RefPath = imagePath },
                out DataObject? imageData),
                "图片应可构造拖出数据");
            Assert(imageData?.GetDataPresent(DataFormats.Bitmap) == true
                && imageData.GetData(DataFormats.FileDrop) is string[] { Length: 1 },
                "图片拖出应同时包含 Bitmap 和 FileDrop");
            Assert(GetPreferredDropEffect(imageData) == DragDropEffects.Copy,
                "图片拖出应明确声明复制效果");

            BitmapSource repairedImage = ClipboardImageNormalizer.Load(imagePath);
            var repairedPixel = new byte[4];
            repairedImage.CopyPixels(repairedPixel, 4, 0);
            Assert(repairedPixel[0] == 0x30 && repairedPixel[1] == 0x70 && repairedPixel[2] == 0xB0
                && repairedPixel[3] == byte.MaxValue,
                "RGB 有内容但 alpha 全零的图片应修复为不透明");

            var transparentBlank = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 0, 0 }, 4);
            BitmapSource unchangedBlank = ClipboardImageNormalizer.NormalizeOpaqueAlpha(transparentBlank, out bool blankChanged);
            Assert(!blankChanged && ReferenceEquals(transparentBlank, unchangedBlank),
                "真正的全透明空白图片不应被误修复");

            Assert(!ClipDragDataBuilder.TryBuild(
                new ClipItem { Type = ClipType.File, RefPath = Path.Combine(dir, "missing-manifest.json") },
                out _),
                "缺失引用不应启动拖出");

            File.WriteAllText(resultPath, "DRAG SELFTEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "DRAG SELFTEST FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>打印真实数据目录中最近的记录（用于无界面确认捕获已入库）。</summary>
    public static int Dump()
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "clipora-dump.txt");
        try
        {
            var paths = new AppPaths();
            var db = new Database(paths);
            var clips = new SqliteClipStore(db);

            var items = clips.Query(new ClipQuery { Take = 20 });
            var sb = new StringBuilder();
            sb.AppendLine("DATA DIR = " + paths.Root);
            sb.AppendLine("COUNT = " + items.Count);
            foreach (var it in items)
                sb.AppendLine($"- [{it.Type}] src={it.SourceApp ?? "?"} :: {it.PreviewText}");

            File.WriteAllText(resultPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "DUMP FAIL: " + ex);
            return 1;
        }
    }

    /// <summary>M5.1 OCR 状态诊断：显示引擎可用性、处理进度与识别结果样本。</summary>
    public static int RunOcrStatus()
    {
        var resultPath = Path.Combine(Path.GetTempPath(), "clipora-ocr-status.txt");
        try
        {
            var paths = new AppPaths();
            var db = new Database(paths);
            var clips = new SqliteClipStore(db);
            var ocr = new WindowsOcrService();
            var settingsService = new SettingsService(paths);

            var sb = new StringBuilder();
            sb.AppendLine("=== Clipora OCR 状态诊断 ===");
            sb.AppendLine("数据目录: " + paths.Root);
            sb.AppendLine();

            // 1. 引擎状态
            sb.AppendLine("--- OCR 引擎 ---");
            sb.AppendLine("IsAvailable: " + ocr.IsAvailable);
            sb.AppendLine("OcrEnabled (设置): " + settingsService.Current.OcrEnabled);
            sb.AppendLine("OcrBackfillCompleted: " + settingsService.Current.OcrBackfillCompleted);
            sb.AppendLine();

            // 2. 图片统计
            var allImages = clips.Query(new ClipQuery { Type = ClipType.Image, Take = 10000 });
            int totalImages = allImages.Count;
            int pending = 0, completed = 0, empty = 0, failed = 0, unsupported = 0, none = 0;
            var completedSamples = new List<string>();

            foreach (var img in allImages)
            {
                switch (img.OcrStatus)
                {
                    case OcrStatus.None: none++; break;
                    case OcrStatus.Pending: pending++; break;
                    case OcrStatus.Completed: completed++; if (completedSamples.Count < 3 && !string.IsNullOrEmpty(img.OcrText)) completedSamples.Add(img.OcrText); break;
                    case OcrStatus.Empty: empty++; break;
                    case OcrStatus.Failed: failed++; break;
                    case OcrStatus.Unsupported: unsupported++; break;
                }
            }

            sb.AppendLine("--- 图片统计 ---");
            sb.AppendLine($"总数: {totalImages}");
            sb.AppendLine($"  None (未处理):       {none}");
            sb.AppendLine($"  Pending (等待识别):  {pending}");
            sb.AppendLine($"  Completed (已识别):  {completed}");
            sb.AppendLine($"  Empty (无文字):      {empty}");
            sb.AppendLine($"  Failed (失败):       {failed}");
            sb.AppendLine($"  Unsupported (不支持): {unsupported}");
            sb.AppendLine();

            // 3. 最近 OCR 结果样本
            if (completedSamples.Count > 0)
            {
                sb.AppendLine("--- 识别结果样本（最多 3 条）---");
                for (int i = 0; i < completedSamples.Count; i++)
                    sb.AppendLine($"[{i + 1}] {completedSamples[i]}");
            }
            else if (pending > 0)
            {
                sb.AppendLine("--- 注意 ---");
                sb.AppendLine($"有 {pending} 张图片待识别，但后台处理可能尚未运行。");
                sb.AppendLine($"OcrEnabled={settingsService.Current.OcrEnabled}, IsAvailable={ocr.IsAvailable}");
                sb.AppendLine("若引擎可用且开关已开启，重启 Clipora 将触发回填。");
            }

            File.WriteAllText(resultPath, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "OCR-STATUS FAIL: " + ex);
            Console.WriteLine("OCR-STATUS FAIL: " + ex.Message);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>
    /// 平滑滚动重构（架构候选 6，行为保持型）的纯函数与编排自检。
    /// 用假 <see cref="IScrollSurface"/> / <see cref="IFrameClock"/> 驱动 <see cref="SmoothScroller"/>，无需真实 WPF。
    /// </summary>
    private static void RunScrollingTests()
    {
        // —— 1. ease-out cubic 边界（0→0 / 1→1 / 起步快）——
        Assert(ScrollEasing.EaseOutCubic(0) == 0, "EaseOutCubic(0) 应为 0");
        Assert(ScrollEasing.EaseOutCubic(1) == 1, "EaseOutCubic(1) 应为 1");
        Assert(ScrollEasing.EaseOutCubic(0.5) > 0.5, "EaseOutCubic 应起步快（0.5 处 > 0.5）");

        // —— 2. OffsetAt：起点对齐 / 到时精确停 / 不过冲（双向）——
        Assert(ScrollEasing.OffsetAt(100, 300, 0, 165) == 100, "elapsed=0 应停在起点");
        Assert(ScrollEasing.OffsetAt(100, 300, 165, 165) == 300, "到时应精确停在目标");
        Assert(ScrollEasing.OffsetAt(100, 300, 1000, 165) == 300, "超时仍精确停在目标，不过冲");
        double mid = ScrollEasing.OffsetAt(100, 300, 80, 165);
        Assert(mid > 100 && mid < 300, "向下过程应落在 (start,target) 之间");
        double midUp = ScrollEasing.OffsetAt(300, 100, 80, 165);
        Assert(midUp < 300 && midUp > 100, "向上过程同样不过冲");

        // —— 3. SmoothScroller 编排：滚轮位移 / 连续累加 / clamp / 不可滚 / CancelAll / GlideTo ——
        // 滚轮向下一格（delta=-120）→ 88px/格 → 落在 88。
        {
            var clock = new FakeFrameClock();
            var scroller = new SmoothScroller(clock);
            var surface = new FakeSurface { Offset = 0, ScrollableExtent = 500 };
            scroller.GlideBy(surface, -120);
            clock.AdvanceMs(165);
            clock.Fire();
            Assert(Math.Abs(surface.Offset - 88) < 0.001, "向下滚一格应停在 88px（88px/格）");

            // 连续滚轮在动画中累加目标（再两格 → 88+88+88=264）。
            scroller.GlideBy(surface, -120);
            scroller.GlideBy(surface, -120);
            clock.AdvanceMs(165);
            clock.Fire();
            Assert(Math.Abs(surface.Offset - 264) < 0.001, "连续滚轮应累加目标（→264）");
        }
        // 目标 clamp 到 [0, 可滚范围]。
        {
            var clock = new FakeFrameClock();
            var scroller = new SmoothScroller(clock);
            var surface = new FakeSurface { Offset = 0, ScrollableExtent = 100 };
            scroller.GlideBy(surface, -1200);
            clock.AdvanceMs(165);
            clock.Fire();
            Assert(Math.Abs(surface.Offset - 100) < 0.001, "目标应 clamp 到可滚范围上界");
        }
        // 不可滚（ScrollableExtent<=0）时滚轮无效。
        {
            var clock = new FakeFrameClock();
            var scroller = new SmoothScroller(clock);
            var surface = new FakeSurface { Offset = 0, ScrollableExtent = 0 };
            scroller.GlideBy(surface, -120);
            clock.AdvanceMs(165);
            clock.Fire();
            Assert(surface.Offset == 0, "不可滚时滚轮应无效");
        }
        // CancelAll 后帧推进不再移动。
        {
            var clock = new FakeFrameClock();
            var scroller = new SmoothScroller(clock);
            var surface = new FakeSurface { Offset = 0, ScrollableExtent = 500 };
            scroller.GlideBy(surface, -120);
            scroller.CancelAll();
            double before = surface.Offset;
            clock.AdvanceMs(165);
            clock.Fire();
            Assert(surface.Offset == before, "CancelAll 后帧推进不应再移动");
        }
        // GlideTo(.,0) 到时精确回到顶部（回到顶部路径）。
        {
            var clock = new FakeFrameClock();
            var scroller = new SmoothScroller(clock);
            var surface = new FakeSurface { Offset = 300, ScrollableExtent = 500 };
            scroller.GlideTo(surface, 0, ScrollGlide.Wheel.WithDuration(400));
            clock.AdvanceMs(400);
            clock.Fire();
            Assert(surface.Offset == 0, "GlideTo(.,0) 到时应精确回到顶部");
        }

        // 快速滚动回收图片卡片后，预览监视器必须把脱离 PresentationSource 视为失效，
        // 不得继续调用 PointToScreen 并让 Dispatcher 未捕获异常终止进程。
        {
            var detachedTarget = new Border { Width = 120, Height = 80 };
            Assert(!ImagePreviewScreenGeometry.TryGetElementBounds(
                    detachedTarget,
                    marginDip: 4,
                    out _),
                "脱离 PresentationSource 的预览目标应 fail closed，不得计算屏幕坐标");
        }

        // —— 4. 回到顶部显隐滞回真值表 ——
        double up = 0, down = 0;
        Assert(BackToTopVisibilityPolicy.Decide(-40, 500, false, ref up, ref down) == BackToTopAction.None,
            "上滚累计不足 64 不应浮出");
        Assert(BackToTopVisibilityPolicy.Decide(-30, 500, false, ref up, ref down) == BackToTopAction.Show,
            "上滚累计过 64 且偏移≥160 应浮出");

        up = 100; down = 0;
        BackToTopVisibilityPolicy.Decide(20, 500, true, ref up, ref down);
        Assert(up == 0, "下滚应清零上累加器（反向清对侧）");

        up = 0; down = 0;
        Assert(BackToTopVisibilityPolicy.Decide(40, 500, true, ref up, ref down) == BackToTopAction.Hide,
            "下滚累计过 32 应收起");

        up = 0; down = 0;
        Assert(BackToTopVisibilityPolicy.Decide(-100, 120, false, ref up, ref down) == BackToTopAction.None,
            "偏移不足 160 即使上滚达阈值也不浮出");

        up = 100; down = 100;
        BackToTopAction topAction = BackToTopVisibilityPolicy.Decide(-10, 0, true, ref up, ref down);
        Assert(up == 0 && down == 0, "触顶应清零两个累加器");
        Assert(topAction == BackToTopAction.Hide, "触顶（可见）应收起");
    }

    private static void RunCliporaCardHitTest()
    {
        Style cardStyle = Application.Current.FindResource("CliporaCard") as Style
            ?? throw new InvalidOperationException("CliporaCard 样式不存在");
        Assert(cardStyle.TargetType == typeof(ContentControl),
            "CliporaCard 应以 ContentControl 承载固定命中层");

        ContentControl CreateCard() => new()
        {
            Style = cardStyle,
            Width = 120,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Content = new Border { Height = 40, Background = Brushes.White },
        };

        var firstCard = CreateCard();
        var secondCard = CreateCard();
        var stack = new StackPanel { Width = 120 };
        stack.Children.Add(firstCard);
        stack.Children.Add(secondCard);
        stack.Measure(new Size(120, double.PositiveInfinity));
        stack.Arrange(new Rect(0, 0, 120, stack.DesiredSize.Height));
        firstCard.ApplyTemplate();
        secondCard.ApplyTemplate();
        stack.UpdateLayout();

        var hitSurface = firstCard.Template.FindName("HoverHitSurface", firstCard) as Border;
        var cardSurface = firstCard.Template.FindName("CardSurface", firstCard) as Border;
        var retentionSurface = firstCard.Template.FindName("HoverRetentionSurface", firstCard) as Border;
        Assert(hitSurface is not null && cardSurface is not null && retentionSurface is not null,
            "CliporaCard 模板应包含固定卡片命中层、可移动视觉层与下边缘 hover 保留带");
        Assert(hitSurface!.RenderTransform.Value.IsIdentity,
            "CliporaCard 固定命中层不得应用位移");
        Assert(cardSurface!.RenderTransform is TranslateTransform,
            "CliporaCard 视觉层应使用 TranslateTransform");
        Assert(Math.Abs(firstCard.ActualHeight - 44) < 0.01
            && Math.Abs(secondCard.TranslatePoint(new Point(), stack).Y - 44) < 0.01,
            "CliporaCard 应保持 40 DIP 卡片 + 4 DIP 原有间距，且相邻卡片不得重叠");

        // 模板中的 Freezable 可能已被冻结；替换为等价本地变换来模拟悬停终态。
        cardSurface.RenderTransform = new TranslateTransform(0, -3);
        stack.UpdateLayout();

        HitTestResult? bottomEdgeHit = VisualTreeHelper.HitTest(stack, new Point(60, 39.5));
        Assert(bottomEdgeHit is not null && ReferenceEquals(bottomEdgeHit.VisualHit, hitSurface),
            "CliporaCard 视觉上移 3 DIP 后，原始下边缘内侧仍应命中固定透明外层");
        HitTestResult? retentionHit = VisualTreeHelper.HitTest(stack, new Point(60, 41.5));
        Assert(retentionHit is not null && ReferenceEquals(retentionHit.VisualHit, retentionSurface),
            "CliporaCard 原下边缘外 3 DIP 内应命中稳定 hover 保留带");
        HitTestResult? clearGapHit = VisualTreeHelper.HitTest(stack, new Point(60, 43.5));
        Assert(clearGapHit is null,
            "相邻卡片之间最后 1 DIP 应保持不可命中，避免 hover 粘连");
        HitTestResult? secondCardHit = VisualTreeHelper.HitTest(stack, new Point(60, 44.5));
        Assert(secondCardHit is not null && IsVisualDescendantOf(secondCardHit.VisualHit, secondCard),
            "越过 4 DIP 间距后应明确命中下一张卡片");
        Assert(Math.Abs(hitSurface.ActualHeight - 40) < 0.01
            && Math.Abs(cardSurface.ActualHeight - 40) < 0.01
            && Math.Abs(retentionSurface!.ActualHeight - 3) < 0.01,
            "CliporaCard 视觉高度、固定命中高度与 3 DIP 保留带应精确保持");
    }

    private static bool IsVisualDescendantOf(DependencyObject visual, DependencyObject ancestor)
    {
        for (DependencyObject? current = visual; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 图片大预览重构自检：覆盖连续悬停、滚动/拖拽抑制、物理屏幕定位和串行限尺寸解码。
    /// </summary>
    private static void RunImagePreviewTests(string tempRoot)
    {
        // —— 1. 连续 350ms 悬停、过期代次和滚动停稳 ——
        var policy = new ImagePreviewInteractionPolicy();
        long firstVersion = policy.ArmFromPointerEntry(1_000);
        Assert(firstVersion > 0 && policy.Phase == ImagePreviewPhase.Pending,
            "ImagePreview: 首次进入应进入 Pending");
        Assert(!policy.ShouldBeginLoading(1_349),
            "ImagePreview: 连续悬停不足 350ms 不得解码");
        Assert(policy.ShouldBeginLoading(1_350),
            "ImagePreview: 连续悬停达到 350ms 才可解码");
        Assert(policy.TryBeginLoading(firstVersion)
            && policy.IsCurrent(firstVersion, ImagePreviewPhase.Loading),
            "ImagePreview: 当前代次应进入 Loading");
        Assert(policy.TryOpen(firstVersion) && policy.Phase == ImagePreviewPhase.Open,
            "ImagePreview: 当前加载结果应进入 Open");

        policy.Cancel();
        Assert(!policy.TryOpen(firstVersion),
            "ImagePreview: 取消后的过期加载结果必须丢弃");
        long switchedVersion = policy.ArmFromPointerEntry(1_500);
        Assert(switchedVersion != firstVersion && !policy.TryBeginLoading(firstVersion),
            "ImagePreview: 切换目标必须使旧请求代次失效");

        policy.NotifyScroll(2_000);
        Assert(policy.IsScrollActive && policy.Phase == ImagePreviewPhase.Suppressed,
            "ImagePreview: 非零滚动应立即进入 Suppressed");
        Assert(!policy.TrySettleScroll(2_119),
            "ImagePreview: 120ms 内仍应视为滚动中");
        Assert(policy.TrySettleScroll(2_120) && policy.Phase == ImagePreviewPhase.Idle,
            "ImagePreview: 最后一次位移后 120ms 才可恢复");
        long afterScrollVersion = policy.ArmAfterScroll(2_120);
        Assert(afterScrollVersion > 0 && !policy.ShouldBeginLoading(2_469),
            "ImagePreview: 滚动时间不得计入新的 350ms 悬停");
        Assert(policy.ShouldBeginLoading(2_470),
            "ImagePreview: 停稳后重新连续等待 350ms 才可加载");

        policy.NotifyPointerInteraction();
        Assert(policy.RequiresFreshEntry && policy.Phase == ImagePreviewPhase.Suppressed,
            "ImagePreview: 鼠标按下/拖拽应取消并要求新的进入");
        Assert(policy.ArmAfterScroll(3_000) < 0,
            "ImagePreview: 松键或滚动停稳不得补弹拖拽前的预览");
        long freshVersion = policy.ArmFromPointerEntry(3_100);
        Assert(freshVersion > 0 && !policy.RequiresFreshEntry,
            "ImagePreview: 新的缩略图进入才解除拖拽抑制");

        // —— 2. 4 DIP 容差必须按各显示器 DPI 转为物理像素 ——
        Rect inflated100 = ImagePreviewScreenGeometry.InflateForDpi(
            new Rect(100, 100, 200, 100),
            marginDip: 4,
            scaleX: 1,
            scaleY: 1);
        Assert(inflated100 == new Rect(96, 96, 208, 108),
            "ImagePreview: 100% DPI 下 4 DIP 容差应等于 4px");
        Rect inflated200 = ImagePreviewScreenGeometry.InflateForDpi(
            new Rect(100, 100, 200, 100),
            marginDip: 4,
            scaleX: 2,
            scaleY: 2);
        Assert(inflated200 == new Rect(92, 92, 216, 116),
            "ImagePreview: 200% DPI 下 4 DIP 容差应等于 8px");
        Assert(inflated100.Contains(97, 150) && !inflated100.Contains(95, 150),
            "ImagePreview: 3 DIP 上浮应留在安全区，超过 4 DIP 应立即判定离开");

        // 125% DPI 真机回归：CustomPopupPlacement 的相对点必须保持与 PointToScreen
        // 相同的设备坐标单位；若再除以 1.25，左侧 Popup 会被向右推回并覆盖主面板。
        var plannedPopup = new Rect(1_567, 522, 500, 323);
        var targetPhysicalOrigin = new Point(2_106, 607);
        Point popupOffset = ImagePreviewScreenGeometry.ToTargetRelativePopupOffset(
            plannedPopup,
            targetPhysicalOrigin);
        Assert(popupOffset == new Point(-539, -85),
            "ImagePreview: Popup 相对偏移不得重复执行 DPI 缩放");
        Assert(targetPhysicalOrigin.X + popupOffset.X == plannedPopup.Left
            && targetPhysicalOrigin.Y + popupOffset.Y == plannedPopup.Top,
            "ImagePreview: Popup 最终物理位置必须精确还原规划矩形");

        string bindingPath = Path.Combine(tempRoot, "preview-binding.png");
        var originalBinding = new ClipItemViewModel(new ClipItem
        {
            Type = ClipType.Image,
            RefPath = bindingPath,
        });
        var recycledBinding = new ClipItemViewModel(new ClipItem
        {
            Type = ClipType.Image,
            RefPath = Path.Combine(tempRoot, "preview-recycled.png"),
        });
        var recycledTarget = new Border { DataContext = originalBinding };
        Assert(ImagePreviewController.IsTargetBindingCurrent(
                recycledTarget,
                originalBinding,
                bindingPath),
            "ImagePreview: 原容器、原绑定和原路径应保持有效");
        recycledTarget.DataContext = recycledBinding;
        Assert(!ImagePreviewController.IsTargetBindingCurrent(
                recycledTarget,
                originalBinding,
                bindingPath),
            "ImagePreview: 虚拟化容器改绑后旧请求必须失效");
        recycledTarget.DataContext = originalBinding;
        Assert(!ImagePreviewController.IsTargetBindingCurrent(
                recycledTarget,
                originalBinding,
                bindingPath + ".changed"),
            "ImagePreview: 请求路径改变后旧请求必须失效");

        // —— 3. 稳定位置：外侧优先、>=60% 缩放、内部降级、负坐标显示器 ——
        static void AssertInside(Rect outer, Rect inner, string message)
        {
            const double epsilon = 0.001;
            Assert(inner.Left >= outer.Left - epsilon
                && inner.Top >= outer.Top - epsilon
                && inner.Right <= outer.Right + epsilon
                && inner.Bottom <= outer.Bottom + epsilon,
                message);
        }

        static void AssertPlacementInvariant(
            Rect work,
            Rect window,
            ImagePreviewPlacement placement,
            double gap,
            string scenario)
        {
            const double epsilon = 0.001;
            AssertInside(work, placement.Bounds,
                $"ImagePreview: {scenario} 结果必须位于工作区内");

            if (placement.Kind == ImagePreviewPlacementKind.ExternalLeft)
            {
                Assert(Math.Abs(window.Left - gap - placement.Bounds.Right) <= epsilon,
                    $"ImagePreview: {scenario} 左侧外置预览必须精确贴齐 12 DIP 面板间距");
                Assert(placement.Bounds.Right <= window.Left - epsilon,
                    $"ImagePreview: {scenario} 左侧外置预览不得覆盖主面板");
            }
            else if (placement.Kind == ImagePreviewPlacementKind.ExternalRight)
            {
                Assert(Math.Abs(placement.Bounds.Left - window.Right - gap) <= epsilon,
                    $"ImagePreview: {scenario} 右侧外置预览必须精确贴齐 12 DIP 面板间距");
                Assert(placement.Bounds.Left >= window.Right + epsilon,
                    $"ImagePreview: {scenario} 右侧外置预览不得覆盖主面板");
            }
        }

        var workArea = new Rect(0, 0, 1920, 1080);
        ImagePreviewPlacement rightPlacement = ImagePreviewPlacementPolicy.Calculate(
            workArea,
            new Rect(40, 100, 500, 700),
            new Rect(120, 300, 160, 100),
            new Size(400, 300),
            gap: 12);
        Assert(rightPlacement.Kind == ImagePreviewPlacementKind.ExternalRight
            && Math.Abs(rightPlacement.Scale - 1) < 0.001,
            "ImagePreview: 面板右侧空间充足时应完整放在右侧外部");
        AssertInside(workArea, rightPlacement.Bounds,
            "ImagePreview: 右侧外部位置必须位于工作区内");

        ImagePreviewPlacement leftPlacement = ImagePreviewPlacementPolicy.Calculate(
            workArea,
            new Rect(1380, 100, 500, 700),
            new Rect(1580, 300, 160, 100),
            new Size(400, 300),
            gap: 12);
        Assert(leftPlacement.Kind == ImagePreviewPlacementKind.ExternalLeft
            && Math.Abs(leftPlacement.Scale - 1) < 0.001,
            "ImagePreview: 面板左侧空间充足时应完整放在左侧外部");
        AssertInside(workArea, leftPlacement.Bounds,
            "ImagePreview: 左侧外部位置必须位于工作区内");

        var constrainedWorkArea = new Rect(0, 0, 1000, 800);
        ImagePreviewPlacement scaledPlacement = ImagePreviewPlacementPolicy.Calculate(
            constrainedWorkArea,
            new Rect(300, 50, 440, 700),
            new Rect(360, 300, 140, 90),
            new Size(400, 400),
            gap: 12);
        Assert(scaledPlacement.Kind == ImagePreviewPlacementKind.ExternalLeft
            && scaledPlacement.Scale >= ImagePreviewPlacementPolicy.MinimumExternalScale
            && scaledPlacement.Scale < 1,
            "ImagePreview: 外侧能容纳至少 60% 时应等比缩小并继续放在外侧");
        AssertInside(constrainedWorkArea, scaledPlacement.Bounds,
            "ImagePreview: 外侧缩小位置必须位于工作区内");

        ImagePreviewPlacement internalPlacement = ImagePreviewPlacementPolicy.Calculate(
            constrainedWorkArea,
            new Rect(20, 20, 960, 760),
            new Rect(100, 280, 140, 90),
            new Size(400, 400),
            gap: 12);
        Assert(internalPlacement.Kind == ImagePreviewPlacementKind.InternalRight,
            "ImagePreview: 两侧均不足 60% 时应放到面板内部且远离左侧目标");
        AssertInside(constrainedWorkArea, internalPlacement.Bounds,
            "ImagePreview: 面板内部降级位置必须位于工作区内");

        var negativeWorkArea = new Rect(-1920, 0, 1920, 1080);
        ImagePreviewPlacement negativePlacement = ImagePreviewPlacementPolicy.Calculate(
            negativeWorkArea,
            new Rect(-1800, 100, 500, 700),
            new Rect(-1700, 300, 160, 100),
            new Size(400, 300),
            gap: 12);
        AssertInside(negativeWorkArea, negativePlacement.Bounds,
            "ImagePreview: 负坐标显示器的位置必须始终位于其工作区内");

        Size desired = ImagePreviewPlacementPolicy.GetDesiredOuterSize(
            pixelWidth: 800,
            pixelHeight: 400,
            displayDpiX: 96,
            displayDpiY: 96,
            maximumOuterSize: 400,
            chromeSize: 20);
        Assert(Math.Abs(desired.Width - 400) < 0.001
            && Math.Abs(desired.Height - 210) < 0.001,
            "ImagePreview: 400 DIP 外框上限应保留图像宽高比和 20 DIP 外框");

        // 几何不变量矩阵：Windows 常见/自定义 100%–500% 缩放、横竖屏、负坐标显示器，
        // 以及从窄窗到最大化的窗口尺寸。只要策略选择 External，必须严格保持 12 DIP
        // 间距且与面板零重叠；空间不足转 Internal 是用户已确认的唯一例外。
        double[] dpiScales =
        [
            1.0, 1.1, 1.2, 1.25, 1.33, 1.5, 1.6, 1.75,
            2.0, 2.25, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0,
        ];
        Rect[] workAreas =
        [
            new Rect(0, 0, 1280, 720),
            new Rect(0, 0, 1920, 1080),
            new Rect(-2560, -200, 2560, 1440),
            new Rect(1920, -300, 1080, 1920),
        ];
        double[] windowWidthRatios = [0.18, 0.30, 0.50, 0.75, 1.0];
        double[] windowHeightRatios = [0.35, 0.65, 1.0];
        double[] horizontalPositions = [0.0, 0.5, 1.0];

        foreach (double dpiScale in dpiScales)
        {
            foreach (Rect matrixWorkArea in workAreas)
            foreach (double widthRatio in windowWidthRatios)
            foreach (double heightRatio in windowHeightRatios)
            foreach (double horizontalPosition in horizontalPositions)
            {
                double windowWidth = matrixWorkArea.Width * widthRatio;
                double windowHeight = matrixWorkArea.Height * heightRatio;
                double windowLeft = matrixWorkArea.Left
                    + (matrixWorkArea.Width - windowWidth) * horizontalPosition;
                double windowTop = matrixWorkArea.Top
                    + (matrixWorkArea.Height - windowHeight) / 2;
                var matrixWindow = new Rect(
                    windowLeft,
                    windowTop,
                    windowWidth,
                    windowHeight);
                double targetWidth = Math.Min(windowWidth * 0.45, 180 * dpiScale);
                double targetHeight = Math.Min(windowHeight * 0.30, 100 * dpiScale);
                bool targetOnLeft = horizontalPosition < 0.5;
                double targetLeft = targetOnLeft
                    ? matrixWindow.Left + windowWidth * 0.08
                    : matrixWindow.Right - windowWidth * 0.08 - targetWidth;
                var matrixTarget = new Rect(
                    targetLeft,
                    matrixWindow.Top + (windowHeight - targetHeight) / 2,
                    targetWidth,
                    targetHeight);
                double matrixGap = 12 * dpiScale;
                var matrixDesired = new Size(400 * dpiScale, 260 * dpiScale);
                ImagePreviewPlacement matrixPlacement = ImagePreviewPlacementPolicy.Calculate(
                    matrixWorkArea,
                    matrixWindow,
                    matrixTarget,
                    matrixDesired,
                    matrixGap);
                string scenario = $"{dpiScale:P0}/{matrixWorkArea.Width}x{matrixWorkArea.Height}/"
                    + $"window={widthRatio:P0}x{heightRatio:P0}/pos={horizontalPosition:P0}";
                AssertPlacementInvariant(
                    matrixWorkArea,
                    matrixWindow,
                    matrixPlacement,
                    matrixGap,
                    scenario);

                Point targetOrigin = matrixTarget.TopLeft;
                Point matrixOffset = ImagePreviewScreenGeometry.ToTargetRelativePopupOffset(
                    matrixPlacement.Bounds,
                    targetOrigin);
                Assert(Math.Abs(targetOrigin.X + matrixOffset.X - matrixPlacement.Bounds.Left) <= 0.001
                    && Math.Abs(targetOrigin.Y + matrixOffset.Y - matrixPlacement.Bounds.Top) <= 0.001,
                    $"ImagePreview: {scenario} Popup 坐标适配必须与 DPI 无关并还原规划位置");
            }
        }

        var smallWorkArea = new Rect(0, 0, 800, 600);
        ImagePreviewPlacement maximizedSmallPlacement = ImagePreviewPlacementPolicy.Calculate(
            smallWorkArea,
            new Rect(0, 0, 800, 600),
            new Rect(80, 220, 120, 80),
            new Size(400, 400),
            gap: 12);
        Assert(maximizedSmallPlacement.Kind == ImagePreviewPlacementKind.InternalRight,
            "ImagePreview: 小屏最大化窗口应降级到面板内部");
        AssertInside(smallWorkArea, maximizedSmallPlacement.Bounds,
            "ImagePreview: 小屏最大化定位结果必须位于工作区内");

        // —— 4. 真实解码：限尺寸、冻结、异常静默和单张缓存 ——
        string previewDir = Path.Combine(tempRoot, "image-preview-tests");
        Directory.CreateDirectory(previewDir);
        string imagePath = Path.Combine(previewDir, "wide.png");
        WritePreviewBitmap(imagePath, width: 800, height: 400);

        using (var loader = new ImagePreviewLoader())
        {
            BitmapSource? decoded = loader.LoadAsync(imagePath, 200, 200)
                .GetAwaiter().GetResult();
            Assert(decoded is not null
                && decoded.IsFrozen
                && decoded.PixelWidth <= 200
                && decoded.PixelHeight <= 200,
                "ImagePreviewLoader: 应按预览上限下采样并返回 Freeze 的 BitmapSource");
            Assert(Math.Abs((double)decoded!.PixelWidth / decoded.PixelHeight - 2.0) < 0.05,
                "ImagePreviewLoader: 下采样必须保持原图宽高比");

            BitmapSource? cached = loader.LoadAsync(imagePath, 200, 200)
                .GetAwaiter().GetResult();
            Assert(ReferenceEquals(decoded, cached),
                "ImagePreviewLoader: 最近一张相同尺寸预览应命中单项缓存");

            Assert(loader.LoadAsync(Path.Combine(previewDir, "missing.png"), 200, 200)
                    .GetAwaiter().GetResult() is null,
                "ImagePreviewLoader: 缺失图片应静默返回空");
            Assert(loader.LoadAsync(@"\\server\share\preview.png", 200, 200)
                    .GetAwaiter().GetResult() is null,
                "ImagePreviewLoader: UNC 图片路径不得触发网络读取");
            string corruptPath = Path.Combine(previewDir, "corrupt.png");
            File.WriteAllText(corruptPath, "not an image");
            Assert(loader.LoadAsync(corruptPath, 200, 200)
                    .GetAwaiter().GetResult() is null,
                "ImagePreviewLoader: 损坏图片应静默返回空");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert(loader.LoadAsync(imagePath, 200, 200, canceled.Token)
                    .GetAwaiter().GetResult() is null,
                "ImagePreviewLoader: 已取消请求应静默返回空");
        }

        // —— 5. 工作线程：STA、串行解码、繁忙时只保留最新等待项 ——
        string firstPath = Path.Combine(previewDir, "first.preview");
        string replacedPath = Path.Combine(previewDir, "replaced.preview");
        string latestPath = Path.Combine(previewDir, "latest.preview");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(replacedPath, "replaced");
        File.WriteAllText(latestPath, "latest");

        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var decodedNames = new List<string>();
        int activeDecoders = 0;
        int maximumConcurrentDecoders = 0;
        bool allDecodesWereSta = true;

        BitmapSource? FakeDecode(
            string path,
            int maxPixelWidth,
            int maxPixelHeight,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref activeDecoders);
            int observedMaximum;
            do
            {
                observedMaximum = Volatile.Read(ref maximumConcurrentDecoders);
                if (active <= observedMaximum)
                    break;
            }
            while (Interlocked.CompareExchange(
                ref maximumConcurrentDecoders,
                active,
                observedMaximum) != observedMaximum);

            try
            {
                allDecodesWereSta &= Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                string name = Path.GetFileName(path);
                decodedNames.Add(name);
                if (string.Equals(name, Path.GetFileName(firstPath), StringComparison.Ordinal))
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5));
                }

                byte[] pixel = { 0x20, 0x80, 0xE0, 0xFF };
                BitmapSource bitmap = BitmapSource.Create(
                    1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
                bitmap.Freeze();
                return cancellationToken.IsCancellationRequested ? null : bitmap;
            }
            finally
            {
                Interlocked.Decrement(ref activeDecoders);
            }
        }

        using (var serializedLoader = new ImagePreviewLoader(FakeDecode))
        {
            Task<BitmapSource?> firstTask = serializedLoader.LoadAsync(firstPath, 100, 100);
            Assert(firstStarted.Wait(TimeSpan.FromSeconds(5)),
                "ImagePreviewLoader: 首个请求应在 STA 工作线程开始解码");

            Task<BitmapSource?> replacedTask = serializedLoader.LoadAsync(replacedPath, 100, 100);
            Task<BitmapSource?> latestTask = serializedLoader.LoadAsync(latestPath, 100, 100);
            Assert(replacedTask.Wait(TimeSpan.FromSeconds(5)) && replacedTask.Result is null,
                "ImagePreviewLoader: 工作线程繁忙时应丢弃被更新请求替换的等待项");

            releaseFirst.Set();
            Assert(firstTask.Wait(TimeSpan.FromSeconds(5)) && firstTask.Result is not null,
                "ImagePreviewLoader: 活动解码应正常串行完成");
            Assert(latestTask.Wait(TimeSpan.FromSeconds(5)) && latestTask.Result is not null,
                "ImagePreviewLoader: 活动解码后应只处理最新等待项");
        }

        Assert(allDecodesWereSta,
            "ImagePreviewLoader: 所有解码必须运行在应用级 STA 工作线程");
        Assert(maximumConcurrentDecoders == 1,
            "ImagePreviewLoader: 不得并行执行多个全图解码");
        Assert(decodedNames.SequenceEqual([
                Path.GetFileName(firstPath),
                Path.GetFileName(latestPath)]),
            "ImagePreviewLoader: 有界队列应只执行活动请求和最新等待请求");
    }

    private static void WritePreviewBitmap(string path, int width, int height)
    {
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                pixels[offset] = (byte)(x % 256);
                pixels[offset + 1] = (byte)(y % 256);
                pixels[offset + 2] = 0x80;
                pixels[offset + 3] = 0xFF;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 时间段分组策略重构（架构候选 7，行为保持型）自检。
    /// 直接驱动 <see cref="ClipGrouping"/>，验证段键 / 缓存复用 / 默认展开 / 捕获展开 / 置顶 / 翻转，
    /// 无需 ICollectionView。用「当天本地 03/12/20 点」构造，避免时区/日期波动。
    /// </summary>
    private static void RunGroupingTests()
    {
        var grouping = new ClipGrouping();
        DateTime dayBase = DateTime.Today; // 本地当天 00:00
        var dawn = new ClipItem { CreatedAt = dayBase.AddHours(3).ToUniversalTime() };   // 凌晨段
        var dawn2 = new ClipItem { CreatedAt = dayBase.AddHours(4).ToUniversalTime() };  // 同段另一条
        var noon = new ClipItem { CreatedAt = dayBase.AddHours(12).ToUniversalTime() };  // 日间段
        var night = new ClipItem { CreatedAt = dayBase.AddHours(20).ToUniversalTime() }; // 晚间段（最新）
        var pinned = new ClipItem { IsPinned = true, CreatedAt = dayBase.AddHours(1).ToUniversalTime() };

        // 段键策略：键为「绝对日期#段」，三段互异；段标签在 header 的 Title 上显示。
        Assert(ClipGrouping.KeyFor(pinned) == ClipGrouping.PinnedKey, "置顶项段键应为「置顶」");
        Assert(ClipGrouping.KeyFor(dawn) != ClipGrouping.KeyFor(noon)
            && ClipGrouping.KeyFor(noon) != ClipGrouping.KeyFor(night)
            && ClipGrouping.KeyFor(dawn) != ClipGrouping.KeyFor(night), "三段段键应互异");
        Assert(grouping.Resolve(dawn).Title.Contains("凌晨"), "凌晨段标题应含「凌晨」");
        Assert(grouping.Resolve(noon).Title.Contains("日间"), "日间段标题应含「日间」");
        Assert(grouping.Resolve(night).Title.Contains("晚间"), "晚间段标题应含「晚间」");

        // 跨天回归（本次 bug）：不同日期的同一段（凌晨）段键必须不同，
        // 否则次日凌晨新内容会并入前一天的凌晨组。
        var dawnNextDay = new ClipItem { CreatedAt = dayBase.AddDays(1).AddHours(3).ToUniversalTime() };
        Assert(ClipGrouping.KeyFor(dawn) != ClipGrouping.KeyFor(dawnNextDay),
            "跨天回归：次日凌晨段键应不同于前一天，不得并入前一天凌晨组");

        // 缓存复用：同段不同条目返回同一 header 实例（折叠态保持的根）
        ClipGroupHeader hDawn = grouping.Resolve(dawn);
        Assert(ReferenceEquals(grouping.Resolve(dawn2), hDawn), "同段应共享同一 header 实例");
        ClipGroupHeader hNoon = grouping.Resolve(noon);
        ClipGroupHeader hNight = grouping.Resolve(night);
        ClipGroupHeader hPin = grouping.Resolve(pinned);
        Assert(hPin.IsPinned && hPin.IsExpanded, "置顶组应恒展开");

        // 默认仅展开最新一段（晚间），其余非置顶折叠，置顶恒展开
        grouping.ApplyDefaultExpansion(new[] { dawn, dawn2, noon, night, pinned });
        Assert(hNight.IsExpanded, "默认应展开最新段（晚间）");
        Assert(!hDawn.IsExpanded, "凌晨段默认应折叠");
        Assert(!hNoon.IsExpanded, "日间段默认应折叠");
        Assert(hPin.IsExpanded, "置顶组默认仍展开");

        // 点击翻转：普通组可翻，置顶组忽略
        grouping.Toggle(hDawn);
        Assert(hDawn.IsExpanded, "翻转应展开折叠的凌晨段");
        grouping.Toggle(hDawn);
        Assert(!hDawn.IsExpanded, "再次翻转应折叠");
        grouping.Toggle(hPin);
        Assert(hPin.IsExpanded, "置顶组翻转无效，恒展开");

        // 捕获展开：把折叠的日间段展开
        grouping.ExpandFor(noon);
        Assert(hNoon.IsExpanded, "捕获应展开其所在段");

        // Clear 后重新解析得到新实例（折叠态不再保留）
        grouping.Clear();
        Assert(!ReferenceEquals(grouping.Resolve(dawn), hDawn), "Clear 后应重建 header 实例");
    }

    /// <summary>
    /// 标签管理编排重构（架构候选 8，行为保持型）自检。
    /// 在隔离临时库上驱动 <see cref="TagManagement"/>，验证改名去重/无变化、上下移重排序边界、改色无变化判定，
    /// 无需 ViewModel / WPF。
    /// </summary>
    private static void RunTagManagementTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "clipora-tagmgmt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var db = new Database(paths);
            var store = new SqliteTagStore(db);
            var mgmt = new TagManagement(store);

            long a = store.Create("alpha", "#111111");
            long b = store.Create("beta", "#222222");
            long c = store.Create("gamma", "#333333");
            Assert(store.List().Select(t => t.Id).SequenceEqual(new[] { a, b, c }),
                "初始顺序应为创建顺序 a,b,c");

            // 改名：重复（含大小写不敏感）/空白 → Rejected；同名 → Unchanged；合法 → Renamed 并写库
            Assert(mgmt.Rename(a, "alpha", "beta") == TagRenameOutcome.Rejected, "改名撞重复应拒绝");
            Assert(mgmt.Rename(a, "alpha", "BETA") == TagRenameOutcome.Rejected, "改名撞重复（大小写不敏感）应拒绝");
            Assert(mgmt.Rename(a, "alpha", "   ") == TagRenameOutcome.Rejected, "改名空白应拒绝");
            Assert(mgmt.Rename(a, "alpha", "alpha") == TagRenameOutcome.Unchanged, "同名应无变化");
            Assert(mgmt.Rename(a, "alpha", "alpha2") == TagRenameOutcome.Renamed, "合法改名应成功");
            Assert(store.List().First(t => t.Id == a).Name == "alpha2", "改名应写库");

            // 上下移：越界返回 false；合法重排序并写库
            Assert(mgmt.Move(b, -1), "上移 beta 应发生");
            Assert(store.List().Select(t => t.Id).SequenceEqual(new[] { b, a, c }), "上移后顺序应为 b,a,c");
            Assert(!mgmt.Move(b, -1), "已在顶部上移应无效");
            Assert(!mgmt.Move(c, 1), "已在底部下移应无效");
            Assert(store.List().Select(t => t.Id).SequenceEqual(new[] { b, a, c }), "越界移动不应改变顺序");

            // 改色：同色（含大小写不敏感）→ false；不同 → true 并写库
            Assert(!mgmt.SetColor(a, "#999999", "#999999"), "同色应无变化");
            Assert(!mgmt.SetColor(a, "#abcdef", "#ABCDEF"), "同色（大小写不敏感）应无变化");
            Assert(mgmt.SetColor(a, "#111111", "#0078D4"), "改色应发生");
            Assert(store.List().First(t => t.Id == a).Color == "#0078D4", "改色应写库");

            // 删除：写库
            mgmt.Delete(a);
            Assert(store.List().All(t => t.Id != a), "删除应写库");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败忽略 */ }
        }
    }

    private static void RunFileNameTrimmingTests()
    {
        // 用各种文件名 + 连续宽度扫描验证修剪后文本确实不溢出可用宽度。
        // 此测试直接复现用户报告的 bug：FormattedText(GDI+) 与 WPF TextBlock(DirectWrite)
        // 测量结果存在微小偏差，极端边界条件下修剪文本实际渲染宽度超出可用宽度，
        // 导致末端扩展名被遮挡。

        string[] fileNames =
        {
            "非常长的项目文档最终版v3.5修订_2026年第一季度财务报告_summary.xlsx",
            "readme.md",
            "this_is_a_very_very_very_long_filename_that_would_definitely_need_truncation_with_extension.txt",
            "a.b.c.d.e.f.g.h.i.j.k.l.m.n.o.p.q.r.s.t.u.v.w.x.y.z.json",
            "short.txt",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.BBB",
            "normal_document.pdf",
            "no_extension",
            ".hidden-file-with-long-name",
            "中文文件名测试导出数据表.csv",
            "1.2.3.4.5.6.7.8.9.0.png",
            "mixed中英mixed文mixed名mixed称mixed长mixed度mixed测mixed试.cs",
            "report_final_v2_2026-06-30_approved_by_manager_revised.docx",
            "src_components_common_utils_helpers_string_extensions_test.cs",
            "微信图片_20260630152847_edited_compressed_final.jpg",
        };

        // 用 Arrange 精确控制 TextBlock 宽度，从极窄到极宽全扫描
        double[] widths = new double[280]; // 20..300 step 1
        for (int i = 0; i < widths.Length; i++)
            widths[i] = 20 + i; // 20px - 299px

        int cases = 0;
        int failures = 0;

        // 预先计算每个文件名的"最小可能文本"（…ext 或 …）宽度
        var minTailWidths = new Dictionary<string, double>();
        var probeBlock = new TextBlock { TextWrapping = TextWrapping.NoWrap, FontSize = 13 };
        foreach (string fileName in fileNames)
        {
            string ext = Path.GetExtension(fileName);
            string minTail = EllipsisStr + ext;
            probeBlock.Text = minTail;
            probeBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            minTailWidths[fileName] = probeBlock.DesiredSize.Width;
        }

        foreach (string fileName in fileNames)
        {
            string ext = Path.GetExtension(fileName);
            double minTailWidth = minTailWidths[fileName];

            foreach (double width in widths)
            {
                // 如果可用宽度比"…扩展名"还窄，物理上不可能完整显示扩展名，
                // 这不是算法 bug，跳过。
                if (width <= minTailWidth + 0.5)
                    continue;

                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.NoWrap,
                    FontSize = 13,
                };

                // 用 Arrange 精确设定 ActualWidth（模拟 WPF 布局结果）
                textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                textBlock.Arrange(new Rect(0, 0, width, 20));

                FileNameTrimming.SetFileName(textBlock, fileName);

                string result = textBlock.Text;
                cases++;

                if (!string.IsNullOrEmpty(result) && textBlock.ActualWidth > 0)
                {
                    // 用 WPF 原生测量验证结果文本不溢出
                    string saved = textBlock.Text;
                    textBlock.Text = result;
                    textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double renderedWidth = textBlock.DesiredSize.Width;
                    textBlock.Text = saved;

                    if (renderedWidth > textBlock.ActualWidth + 0.5)
                    {
                        failures++;
                        if (failures <= 10)
                            Console.WriteLine(
                                $"FAIL overflow: '{fileName}' @{width:F0}px → '{result}' " +
                                $"w={renderedWidth:F2} > avail={textBlock.ActualWidth:F2}");
                    }

                    // 有扩展名且文本被截断 → 扩展名必须完整出现在末尾
                    if (ext.Length > 0 && result != fileName)
                    {
                        if (!result.EndsWith(ext, StringComparison.Ordinal))
                        {
                            failures++;
                            if (failures <= 10)
                                Console.WriteLine(
                                    $"FAIL ext: '{fileName}' @{width:F0}px → '{result}' " +
                                    $"missing '{ext}'");
                        }
                    }

                    // 无扩展名且文本被截断 → 末尾应是省略号
                    if (ext.Length == 0 && result != fileName)
                    {
                        if (!result.EndsWith(EllipsisStr, StringComparison.Ordinal))
                        {
                            failures++;
                            if (failures <= 10)
                                Console.WriteLine(
                                    $"FAIL no-ext: '{fileName}' @{width:F0}px → '{result}'");
                        }
                    }
                }
            }
        }

        Assert(failures == 0,
            $"FileNameTrimming 所有测试用例应通过（{cases} cases, {failures} failures）");
    }

    private const string EllipsisStr = "…";

    /// <summary>自检用假滚动面：内存读写偏移，不依赖 WPF。</summary>
    private sealed class FakeSurface : IScrollSurface
    {
        public double Offset { get; set; }
        public double ScrollableExtent { get; set; }
    }

    /// <summary>自检用假帧时钟：手动推进时间戳（与 <see cref="Stopwatch.Frequency"/> 同刻度）并手动触发帧。</summary>
    private sealed class FakeFrameClock : IFrameClock
    {
        public event Action? Tick;
        public long Timestamp { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void AdvanceMs(double ms) => Timestamp += (long)(ms * Stopwatch.Frequency / 1000.0);
        public void Fire() => Tick?.Invoke();
    }

    private static DragDropEffects GetPreferredDropEffect(DataObject? data)
    {
        if (data?.GetData("Preferred DropEffect", autoConvert: false) is not MemoryStream stream)
            return DragDropEffects.None;

        byte[] bytes = stream.ToArray();
        return bytes.Length >= sizeof(int)
            ? (DragDropEffects)BitConverter.ToInt32(bytes, 0)
            : DragDropEffects.None;
    }

    private static void RunClipboardRoundTripWithRetry(
        AppPaths paths, SettingsService settings, IClipStore clips)
    {
        COMException? lastClipboardError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                RunClipboardRoundTrip(paths, settings, clips);
                return;
            }
            catch (COMException ex) when (unchecked((uint)ex.HResult) == 0x800401D0)
            {
                lastClipboardError = ex;
                Thread.Sleep(100 + attempt * 50);
            }
        }

        throw new InvalidOperationException(
            "真实剪贴板集成自检连续 5 次无法打开系统剪贴板", lastClipboardError);
    }

    private static void RunClipboardRoundTrip(AppPaths paths, SettingsService settings, IClipStore clips)
    {
        System.Windows.IDataObject? previous = null;
        try { previous = Clipboard.GetDataObject(); } catch { }

        try
        {
            var sourceResolver = new SelfTestSourceResolver();
            var classifier = new ContentClassifier(new ThumbnailService(paths), sourceResolver, paths, settings);
            var monitor = new SelfTestMonitor();
            var writer = new ClipWriter();

            string filePath = Path.Combine(paths.Root, "clipboard-file.txt");
            File.WriteAllText(filePath, "clipboard payload");
            var files = new StringCollection { filePath };
            Clipboard.SetFileDropList(files);

            ClipItem fileItem = classifier.Classify() ?? throw new InvalidOperationException("文件剪贴板应可分类");
            Assert(fileItem.Type == ClipType.File, "FileDrop 应识别为文件类型");
            Assert(fileItem.SourceApp == sourceResolver.OwnerAppName,
                "剪贴板内容来源应优先使用 clipboard owner，而不是前台应用");
            Assert(fileItem.RefPath is not null && File.Exists(fileItem.RefPath), "文件清单应落盘");
            string fileManifestPath = fileItem.RefPath!;
            ClipFileManifest fileManifest = ClipFileManifest.Load(fileManifestPath)!;
            Assert(!fileManifest.IsReferenceOnly, "小文件应复制副本而非仅引用");
            Assert(fileManifest.GetAvailablePaths().Count == 1, "文件清单应返回可写回路径");

            var externalDropData = new DataObject();
            externalDropData.SetFileDropList(new StringCollection { filePath });
            ClipItem externalFileItem = classifier.Classify(externalDropData)
                ?? throw new InvalidOperationException("外部 FileDrop 应可分类");
            Assert(externalFileItem.Type == ClipType.File, "外部 FileDrop 应识别为文件类型");
            Assert(externalFileItem.RefPath is not null && File.Exists(externalFileItem.RefPath),
                "外部 FileDrop 文件清单应落盘");

            // 单个可渲染图片文件：仍是 File 类型，但应额外生成缩略图，使文件卡片可预览。
            byte[] imageFilePixels =
            {
                0x10, 0x20, 0x30, 0xFF, 0x60, 0x70, 0x80, 0xFF,
                0x40, 0x80, 0xA0, 0xFF, 0x50, 0x90, 0xB0, 0xFF,
            };
            BitmapSource imageFileBitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, imageFilePixels, 8);
            imageFileBitmap.Freeze();
            string imageFilePath = Path.Combine(paths.Root, "dropped-image.png");
            ClipboardImageNormalizer.SavePng(imageFileBitmap, imageFilePath);

            var imageFileDropData = new DataObject();
            imageFileDropData.SetFileDropList(new StringCollection { imageFilePath });
            ClipItem imageFileItem = classifier.Classify(imageFileDropData)
                ?? throw new InvalidOperationException("图片文件 FileDrop 应可分类");
            Assert(imageFileItem.Type == ClipType.File, "图片文件应仍是文件类型（不归入图片，避免与截图混淆）");
            Assert(imageFileItem.ThumbnailPath is not null && File.Exists(imageFileItem.ThumbnailPath),
                "图片文件应额外生成缩略图，使文件卡片可预览");

            // 非图片文件（.txt）不应生成缩略图。
            Assert(externalFileItem.ThumbnailPath is null, "非图片文件不应生成缩略图");

            using var importMonitor = new ClipboardMonitorService(clips, classifier, settings, sourceResolver);
            ClipItem? importedItem = null;
            importMonitor.ClipCaptured += (_, item) => importedItem = item;
            Assert(importMonitor.Import(externalDropData), "外部 FileDrop 应成功进入统一入库链路");
            Assert(importedItem is { Id: > 0, Type: ClipType.File }, "外部 FileDrop 入库后应通知 UI");
            Assert(clips.GetById(importedItem!.Id) is not null, "外部 FileDrop 应持久化到数据库");

            var externalTextData = new DataObject();
            externalTextData.SetText("external plain text", TextDataFormat.UnicodeText);
            Assert(classifier.Classify(externalTextData)?.Type == ClipType.Text,
                "外部 UnicodeText 应识别为文字");
            importedItem = null;
            Assert(importMonitor.Import(externalTextData), "外部文字应成功进入统一入库链路");
            Assert(importedItem is { Id: > 0, Type: ClipType.Text }, "外部文字入库后应通知 UI");

            var externalUrlData = new DataObject();
            externalUrlData.SetText("https://example.com/external", TextDataFormat.UnicodeText);
            Assert(classifier.Classify(externalUrlData)?.Type == ClipType.Url,
                "外部 URL 文本应识别为链接");

            var externalColorData = new DataObject();
            externalColorData.SetText("#3B82F6", TextDataFormat.UnicodeText);
            Assert(classifier.Classify(externalColorData)?.Type == ClipType.Color,
                "外部颜色文本应识别为颜色");

            var externalCodeData = new DataObject();
            externalCodeData.SetText("public class Demo { return; }", TextDataFormat.UnicodeText);
            Assert(classifier.Classify(externalCodeData)?.Type == ClipType.Code,
                "外部代码文本应识别为代码");

            byte[] imagePixels =
            {
                0x20, 0x40, 0x80, 0xFF, 0x30, 0x60, 0x90, 0xFF,
                0x40, 0x80, 0xA0, 0xFF, 0x50, 0x90, 0xB0, 0xFF,
            };
            BitmapSource externalImage = BitmapSource.Create(
                2, 2, 96, 96, PixelFormats.Bgra32, null, imagePixels, 8);
            externalImage.Freeze();

            var externalBitmapData = new DataObject();
            externalBitmapData.SetData(DataFormats.Bitmap, externalImage, autoConvert: false);
            ClipItem externalBitmapItem = classifier.Classify(externalBitmapData)
                ?? throw new InvalidOperationException("外部 Bitmap 应可分类");
            Assert(externalBitmapItem.Type == ClipType.Image, "外部 Bitmap 应识别为图片");
            Assert(externalBitmapItem.RefPath is not null && File.Exists(externalBitmapItem.RefPath)
                && externalBitmapItem.ThumbnailPath is not null && File.Exists(externalBitmapItem.ThumbnailPath),
                "外部 Bitmap 应保存原图与缩略图");
            importedItem = null;
            Assert(importMonitor.Import(externalBitmapData), "外部 Bitmap 应成功进入统一入库链路");
            Assert(importedItem is { Id: > 0, Type: ClipType.Image }, "外部 Bitmap 入库后应通知 UI");

            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(externalImage));
            using var pngStream = new MemoryStream();
            pngEncoder.Save(pngStream);
            var externalPngData = new DataObject();
            externalPngData.SetData("PNG", new MemoryStream(pngStream.ToArray()), autoConvert: false);
            Assert(classifier.Classify(externalPngData)?.Type == ClipType.Image,
                "外部 PNG 流应识别为图片");

            var bmpEncoder = new BmpBitmapEncoder();
            bmpEncoder.Frames.Add(BitmapFrame.Create(externalImage));
            using var bmpStream = new MemoryStream();
            bmpEncoder.Save(bmpStream);
            byte[] bmpBytes = bmpStream.ToArray();
            var externalDibData = new DataObject();
            externalDibData.SetData(
                DataFormats.Dib,
                new MemoryStream(bmpBytes[14..]),
                autoConvert: false);
            Assert(classifier.Classify(externalDibData)?.Type == ClipType.Image,
                "外部 DIB 流应识别为图片");

            const string externalHtml =
                "Version:1.0\r\n<html><body><!--StartFragment--><b>Hello</b> &amp; world<!--EndFragment--></body></html>";
            var externalHtmlData = new DataObject();
            externalHtmlData.SetData(DataFormats.Html, externalHtml, autoConvert: false);
            ClipItem externalHtmlItem = classifier.Classify(externalHtmlData)
                ?? throw new InvalidOperationException("外部 HTML-only 应可分类");
            Assert(externalHtmlItem.Type == ClipType.RichText, "外部 HTML-only 应识别为富文本");
            Assert(externalHtmlItem.TextContent == "Hello & world", "HTML-only 应本地提取预览文字并解码实体");
            Assert(externalHtmlItem.RefPath is not null
                && string.Equals(Path.GetExtension(externalHtmlItem.RefPath), ".html", StringComparison.OrdinalIgnoreCase)
                && File.Exists(externalHtmlItem.RefPath),
                "HTML-only 应保存原始 sidecar");
            importedItem = null;
            Assert(importMonitor.Import(externalHtmlData), "外部 HTML-only 应成功进入统一入库链路");
            Assert(importedItem is { Id: > 0, Type: ClipType.RichText }, "外部 HTML-only 入库后应通知 UI");

            var externalRtfData = new DataObject();
            externalRtfData.SetData(DataFormats.Rtf, @"{\rtf1\ansi Rich \b text\b0}", autoConvert: false);
            ClipItem externalRtfItem = classifier.Classify(externalRtfData)
                ?? throw new InvalidOperationException("外部 RTF-only 应可分类");
            Assert(externalRtfItem.Type == ClipType.RichText, "外部 RTF-only 应识别为富文本");
            Assert(externalRtfItem.TextContent?.Contains("Rich text", StringComparison.Ordinal) == true,
                "RTF-only 应使用 WPF TextRange 提取预览文字");
            Assert(externalRtfItem.RefPath is not null
                && string.Equals(Path.GetExtension(externalRtfItem.RefPath), ".rtf", StringComparison.OrdinalIgnoreCase)
                && File.Exists(externalRtfItem.RefPath),
                "RTF-only 应保存原始 sidecar");

            var richWithTextFallback = new DataObject();
            richWithTextFallback.SetText("https://example.com/keeps-text-priority", TextDataFormat.UnicodeText);
            richWithTextFallback.SetData(DataFormats.Html, "<b>ignored rich fallback</b>", autoConvert: false);
            Assert(classifier.Classify(richWithTextFallback)?.Type == ClipType.Url,
                "存在纯文本 fallback 时应保持已验收的文本/URL 优先级");

            string multiFilePreview = ContentClassifier.MakeFilePreview(
                new[]
                {
                    new ClipFileManifestEntry { DisplayName = "alpha.png" },
                    new ClipFileManifestEntry { DisplayName = "beta.pdf" },
                    new ClipFileManifestEntry { DisplayName = "gamma.txt" },
                    new ClipFileManifestEntry { DisplayName = "delta.pptx" },
                },
                false);
            Assert(multiFilePreview == "共 4 个文件\nalpha.png\nbeta.pdf",
                "多文件预览应固定为数量行 + 前两个名称");

            writer.Write(fileItem);
            Assert(ClipboardInternalWriteMarker.IsPresentOnClipboard(),
                "ClipWriter 写回数据应附加内部标记，避免卡片重用刷新历史时间");
            Assert(Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Count == 1, "文件应写回 FileDrop");

            var richUrlData = new DataObject();
            richUrlData.SetText("https://example.com/path", TextDataFormat.UnicodeText);
            richUrlData.SetData(DataFormats.Rtf, @"{\rtf1 https://example.com/path}");
            ClipItem urlItem = classifier.ClassifyClipboardDataForTest(
                richUrlData, sourceResolver.OwnerAppName)
                ?? throw new InvalidOperationException("RTF URL clipboard should classify");
            Assert(urlItem.Type == ClipType.Url, "RTF URL should be stored as URL");

            var richColorData = new DataObject();
            richColorData.SetText("#3B82F6", TextDataFormat.UnicodeText);
            richColorData.SetData(DataFormats.Rtf, @"{\rtf1 #3B82F6}");
            ClipItem colorItem = classifier.ClassifyClipboardDataForTest(
                richColorData, sourceResolver.OwnerAppName)
                ?? throw new InvalidOperationException("RTF color clipboard should classify");
            Assert(colorItem.Type == ClipType.Color, "RTF color should be stored as color");

            const string codeText = "const answer = compute();";
            var richCodeData = new DataObject();
            richCodeData.SetText(codeText, TextDataFormat.UnicodeText);
            richCodeData.SetData(DataFormats.Rtf, @"{\rtf1 const answer = compute();}");
            // 7111203 起 Classify() 以 clipboard owner 作来源；编辑器识别须设 OwnerAppName（而非前台 AppName）。
            sourceResolver.OwnerAppName = "Visual Studio Code";
            ClipItem codeItem = classifier.ClassifyClipboardDataForTest(
                richCodeData, sourceResolver.OwnerAppName)
                ?? throw new InvalidOperationException("Known editor RTF code should classify");
            Assert(codeItem.Type == ClipType.Code,
                $"Known editor RTF code should be stored as code (actual: {codeItem.Type})");
            Assert(codeItem.TextContent == codeText, "Code should preserve its plain text");

            const string cssText = ":root {\n  --background: #F8FAFC;\n  color: #111827;\n}";
            var richCssData = new DataObject();
            richCssData.SetText(cssText, TextDataFormat.UnicodeText);
            richCssData.SetData(DataFormats.Rtf, @"{\rtf1 :root { --background: #F8FAFC; color: #111827; }}");
            ClipItem cssItem = classifier.ClassifyClipboardDataForTest(
                richCssData, sourceResolver.OwnerAppName)
                ?? throw new InvalidOperationException("Known editor RTF CSS should classify");
            Assert(cssItem.Type == ClipType.Code,
                $"Known editor RTF CSS should be stored as code (actual: {cssItem.Type}, text: {cssItem.TextContent})");

            // 重置剪贴板 owner 为非编辑器，验证非编辑器来源的 RTF 不被提升为代码。
            sourceResolver.OwnerAppName = "Clipora SelfTest";
            ClipItem nonEditorCodeItem = classifier.ClassifyClipboardDataForTest(
                richCodeData, sourceResolver.OwnerAppName)
                ?? throw new InvalidOperationException("Non-editor RTF should classify");
            Assert(nonEditorCodeItem.Type == ClipType.RichText, "Non-editor RTF should not be promoted to code");

            var richData = new DataObject();
            richData.SetText("rich plain", TextDataFormat.UnicodeText);
            richData.SetData(DataFormats.Rtf, @"{\rtf1 rich plain}");
            ClipItem richItem = classifier.ClassifyClipboardDataForTest(
                richData, sourceResolver.OwnerAppName)
                ?? throw new InvalidOperationException("富文本剪贴板应可分类");
            Assert(richItem.Type == ClipType.RichText, "RTF 应识别为富文本类型");
            Assert(richItem.TextContent == "rich plain", "富文本应保留纯文本 fallback");
            Assert(richItem.RefPath is not null && Path.GetExtension(richItem.RefPath).Equals(".rtf", StringComparison.OrdinalIgnoreCase), "富文本应存 RTF sidecar");

            writer.Write(richItem);
            var written = Clipboard.GetDataObject();
            Assert(written?.GetDataPresent(DataFormats.Rtf) == true, "富文本应写回 RTF 格式");
            Assert(Clipboard.GetText(TextDataFormat.UnicodeText) == "rich plain", "富文本应同时写回纯文本");
        }
        finally
        {
            if (previous is not null)
            {
                try { Clipboard.SetDataObject(previous, copy: true); } catch { }
            }
        }
    }

    private sealed class SelfTestSourceResolver : ISourceResolver
    {
        public string? OwnerAppName { get; set; } = "Clipora Clipboard Owner";
        public string? AppName { get; set; } = "Clipora Foreground";
        public string? ProcessName { get; set; } = "clipora";

        public string? GetClipboardOwnerAppName() => OwnerAppName ?? AppName;
        public string? GetForegroundAppName() => AppName;
        public string? GetForegroundProcessName() => ProcessName;
    }

    private sealed class SelfTestMonitor : IClipboardMonitor
    {
        public event EventHandler<ClipItem>? ClipCaptured;
        public event EventHandler<long>? ItemOverSized;

        public void Start() { }
        public void Stop() { }
        public bool Import(System.Windows.IDataObject dataObject) => false;
        public void Dispose() { }

        public void RaiseClipCaptured(ClipItem item) => ClipCaptured?.Invoke(this, item);
        public void RaiseItemOverSized(long sizeBytes) => ItemOverSized?.Invoke(this, sizeBytes);
    }

    private static void RunCustomBackgroundSettingsViewModelTests(string tempRoot, IAutoStartService autoStart)
    {
        string root = Path.Combine(tempRoot, "custom-background-root");
        string sourcePng = Path.Combine(tempRoot, "custom-background-source.png");
        string sourceJpg = Path.Combine(tempRoot, "custom-background-source.jpg");
        WriteTinyBitmap(sourcePng, jpeg: false);
        WriteTinyBitmap(sourceJpg, jpeg: true);

        var settings = new SelfTestSettingsService();
        var vm = new SettingsViewModel(settings, autoStart, root);

        vm.CustomBackgroundOpacity = -10;
        Assert(settings.Current.CustomBackgroundOpacity == 0 && vm.CustomBackgroundOpacity == 0,
            "CustomBackgroundOpacity should clamp below range to 0");

        vm.CustomBackgroundOpacity = 130;
        Assert(settings.Current.CustomBackgroundOpacity == 100 && vm.CustomBackgroundOpacity == 100,
            "CustomBackgroundOpacity should clamp above range to 100");

        vm.CustomBackgroundOpacitySlider = 37.6;
        Assert(settings.Current.CustomBackgroundOpacity == 38 && vm.CustomBackgroundOpacityText == "38",
            "CustomBackgroundOpacitySlider should round smoothly dragged values for persistence");

        Assert(vm.LiquidGlassTransparency == 55,
            "LiquidGlassTransparency should default to 55");
        var themeSpy = new SelfTestThemeService();
        var glassVm = new SettingsViewModel(settings, autoStart, root, null, false, null, null, null, null, themeSpy);
        glassVm.LiquidGlassTransparency = -12;
        Assert(settings.Current.LiquidGlassTransparency == 0 && glassVm.LiquidGlassTransparency == 0,
            "LiquidGlassTransparency should clamp below range to 0");
        Assert(themeSpy.LastLiquidGlassTransparency == 0,
            "LiquidGlassTransparency setter should apply clamped value to theme service");

        glassVm.LiquidGlassTransparency = 140;
        Assert(settings.Current.LiquidGlassTransparency == 100 && glassVm.LiquidGlassTransparencyText == "100",
            "LiquidGlassTransparency should clamp above range to 100");

        glassVm.LiquidGlassTransparencySlider = 54.6;
        Assert(settings.Current.LiquidGlassTransparency == 55 && glassVm.LiquidGlassTransparencyText == "55",
            "LiquidGlassTransparencySlider should round smoothly dragged values for persistence");

        vm.CustomBackgroundOpacity = 0;
        CustomBackgroundApplyResult first = vm.SetCustomBackground(sourcePng);
        Assert(first.Succeeded && first.Error == CustomBackgroundError.None,
            $"SetCustomBackground should accept a valid PNG: {first.Detail}");
        Assert(settings.Current.CustomBackgroundPath == Path.Combine("backgrounds", "custom-bg.png"),
            "SetCustomBackground should persist a managed relative PNG path");
        Assert(vm.CustomBackgroundFullPath is not null && File.Exists(vm.CustomBackgroundFullPath),
            "SetCustomBackground should copy the image under the data root");
        Assert(vm.CustomBackgroundOpacity == 50,
            "First custom background selection should reset opacity to the default 50");

        string? pngManagedPath = vm.CustomBackgroundFullPath;
        vm.CustomBackgroundOpacity = 42;
        CustomBackgroundApplyResult second = vm.SetCustomBackground(sourceJpg);
        Assert(second.Succeeded && second.Error == CustomBackgroundError.None,
            $"SetCustomBackground should accept a valid JPG: {second.Detail}");
        Assert(settings.Current.CustomBackgroundPath == Path.Combine("backgrounds", "custom-bg.jpg"),
            "JPG selection should persist as custom-bg.jpg");
        Assert(vm.CustomBackgroundOpacity == 42,
            "Reselecting a custom background should preserve opacity");
        Assert(pngManagedPath is not null && !File.Exists(pngManagedPath),
            "Reselecting with another extension should remove the old managed background");

        string pathBeforeReject = settings.Current.CustomBackgroundPath!;
        string unsupported = Path.Combine(tempRoot, "custom-background-source.bmp");
        File.WriteAllText(unsupported, "not a supported background type");
        CustomBackgroundApplyResult unsupportedResult = vm.SetCustomBackground(unsupported);
        Assert(!unsupportedResult.Succeeded && unsupportedResult.Error == CustomBackgroundError.UnsupportedFormat,
            "SetCustomBackground should reject non JPG/PNG extensions");
        Assert(settings.Current.CustomBackgroundPath == pathBeforeReject,
            "Rejected extensions should not change the current background path");

        string tooLarge = Path.Combine(tempRoot, "custom-background-large.jpg");
        using (FileStream large = File.Create(tooLarge))
            large.SetLength(SettingsViewModel.CustomBackgroundMaxBytes + 1);
        CustomBackgroundApplyResult tooLargeResult = vm.SetCustomBackground(tooLarge);
        Assert(!tooLargeResult.Succeeded && tooLargeResult.Error == CustomBackgroundError.TooLarge,
            "SetCustomBackground should reject files over 67 MB before decoding");
        Assert(settings.Current.CustomBackgroundPath == pathBeforeReject,
            "Oversized files should not change the current background path");

        string corrupt = Path.Combine(tempRoot, "custom-background-corrupt.png");
        File.WriteAllText(corrupt, "not an image");
        CustomBackgroundApplyResult corruptResult = vm.SetCustomBackground(corrupt);
        Assert(!corruptResult.Succeeded && corruptResult.Error == CustomBackgroundError.DecodeFailed,
            "SetCustomBackground should reject unreadable JPG/PNG files");
        Assert(settings.Current.CustomBackgroundPath == pathBeforeReject,
            "Unreadable images should not change the current background path");

        string? managedJpg = vm.CustomBackgroundFullPath;
        vm.ClearCustomBackground();
        Assert(settings.Current.CustomBackgroundPath is null && vm.CustomBackgroundOpacity == 0,
            "ClearCustomBackground should clear the path and set opacity to 0");
        Assert(managedJpg is not null && !File.Exists(managedJpg),
            "ClearCustomBackground should delete the managed file");
    }

    private static void WriteTinyBitmap(string path, bool jpeg)
    {
        byte[] pixels =
        {
            0x20, 0x80, 0xE0, 0xFF,
            0x90, 0x50, 0x20, 0xFF,
            0x40, 0xB0, 0x70, 0xFF,
            0xD0, 0xD0, 0xD0, 0xFF,
        };
        BitmapSource bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            8);

        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class SelfTestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new AppSettings();
        public int SaveCount { get; private set; }
        public event EventHandler? Changed;

        public void Save()
        {
            SaveCount++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class SelfTestThemeService : IThemeService
    {
        public string? LastColorMode { get; private set; }
        public string? LastVisualTheme { get; private set; }
        public int? LastLiquidGlassTransparency { get; private set; }

        public void ApplyColorMode(string mode) => LastColorMode = mode;
        public void ApplyVisualTheme(string theme) => LastVisualTheme = theme;
        public void ApplyLiquidGlassTransparency(int value) => LastLiquidGlassTransparency = value;
    }

    /// <summary>自检桩：始终抛指定异常，模拟注册表类型错误等 locator 故障。</summary>
    private sealed class ThrowingStorageRootLocator : IStorageRootLocator
    {
        private readonly StorageLocationException _ex;
        public ThrowingStorageRootLocator(StorageLocationException ex) => _ex = ex;
        public string? GetDataRoot() => throw _ex;
    }

    // —— v0.4.3 外部打开策略自检 ——

    private static void RunExternalOpenPolicyTests()
    {
        Assert(ExternalOpenPolicy.EvaluateUrl("https://example.com") == ExternalOpenDecision.Allow,
            "ExternalOpenPolicy: https 应允许");
        Assert(ExternalOpenPolicy.EvaluateUrl("http://example.com") == ExternalOpenDecision.Allow,
            "ExternalOpenPolicy: http 应允许");
        Assert(ExternalOpenPolicy.EvaluateUrl("file:///C:/secret.txt") == ExternalOpenDecision.Reject,
            "ExternalOpenPolicy: file URL 应拒绝");
        Assert(ExternalOpenPolicy.EvaluateUrl("javascript:alert(1)") == ExternalOpenDecision.Reject,
            "ExternalOpenPolicy: 非 HTTP URL 应拒绝");

        var launcher = new FakeExternalLauncher();
        var coordinator = new ExternalOpenCoordinator(launcher);
        ExternalOpenRequest? safeRequest = coordinator.OpenFile("C:\\Temp\\safe.txt", "fail");
        Assert(safeRequest is null && launcher.Targets.SequenceEqual(["C:\\Temp\\safe.txt"]),
            "ExternalOpenPolicy: 安全文件应直接调用 launcher 一次");

        string[] dangerousExtensions =
        [
            ".exe", ".com", ".scr", ".cpl", ".msi", ".msp", ".bat", ".cmd",
            ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".lnk", ".url", ".reg", ".application", ".appref-ms",
        ];
        foreach (string extension in dangerousExtensions)
        {
            int before = launcher.Targets.Count;
            ExternalOpenRequest? request = coordinator.OpenFile("C:\\Temp\\danger" + extension, "fail");
            Assert(request is not null && launcher.Targets.Count == before,
                $"ExternalOpenPolicy: {extension} 应进入确认且取消时 launcher=0");
        }

        ExternalOpenRequest confirmed = coordinator.OpenFile("C:\\Temp\\confirm.cmd", "fail")!.Value;
        int beforeConfirm = launcher.Targets.Count;
        coordinator.Confirm(confirmed);
        Assert(launcher.Targets.Count == beforeConfirm + 1
            && launcher.Targets[^1] == "C:\\Temp\\confirm.cmd",
            "ExternalOpenPolicy: 明确确认后 fake launcher 应精确调用一次");
    }

    private sealed class FakeExternalLauncher : IExternalLauncher
    {
        internal List<string> Targets { get; } = new();
        public void Launch(string target) => Targets.Add(target);
    }

    private static void RunCrashDiagnosticTests(string tempRoot)
    {
        string releaseRoot = Path.Combine(tempRoot, "diagnostic-release-root");
        string diagnostics = Path.Combine(releaseRoot, "diagnostics");
        Directory.CreateDirectory(diagnostics);
        string legacy = Path.Combine(tempRoot, "legacy-clipora-crash.txt");
        File.WriteAllText(legacy, "legacy-sensitive");

        for (int index = 0; index < 25; index++)
        {
            string dummy = Path.Combine(diagnostics, $"diagnostic-dummy-{index:D2}.json");
            File.WriteAllText(dummy, "{}");
            File.SetLastWriteTimeUtc(dummy, DateTime.UtcNow.AddMinutes(-index));
        }
        string expired = Path.Combine(diagnostics, "diagnostic-expired.json");
        File.WriteAllText(expired, "{}");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));

        var release = new CrashDiagnosticService(
            releaseMode: true,
            releaseRootOverride: releaseRoot,
            legacyCrashPathOverride: legacy);
        release.Initialize();
        Assert(!File.Exists(legacy), "CrashDiagnostic: 固定旧 %TEMP% 日志应升级即删除");
        Assert(!File.Exists(expired), "CrashDiagnostic: 7 天以上日志应删除");
        Assert(Directory.GetFiles(diagnostics, "diagnostic-*").Length <= 20,
            "CrashDiagnostic: Release 诊断应最多保留 20 份");

        const string clipboardSentinel = "CLIPBOARD_SECRET_79F6";
        const string userSentinel = "SensitiveUser_42A1";
        string pathSentinel = "C:" + Path.DirectorySeparatorChar + "Users"
            + Path.DirectorySeparatorChar + userSentinel
            + Path.DirectorySeparatorChar + "private.txt";
        var exception = new InvalidOperationException(
            $"{clipboardSentinel} user={userSentinel} path={pathSentinel}");
        string? releasePath = release.WriteException(exception, "SELFTEST_RELEASE_CRASH");
        Assert(releasePath is not null && File.Exists(releasePath),
            "CrashDiagnostic: Release 应写脱敏 JSON");
        string releaseJson = File.ReadAllText(releasePath!);
        Assert(!releaseJson.Contains(clipboardSentinel, StringComparison.Ordinal)
            && !releaseJson.Contains(userSentinel, StringComparison.Ordinal)
            && !releaseJson.Contains(pathSentinel, StringComparison.Ordinal)
            && !releaseJson.Contains("Message", StringComparison.OrdinalIgnoreCase)
            && !releaseJson.Contains("StackTrace", StringComparison.OrdinalIgnoreCase),
            "CrashDiagnostic: Release JSON 不得泄露 sentinel、Message 或堆栈");
        Assert(releaseJson.Contains("SELFTEST_RELEASE_CRASH", StringComparison.Ordinal)
            && releaseJson.Contains("System.InvalidOperationException", StringComparison.Ordinal),
            "CrashDiagnostic: Release JSON 应保留稳定错误码与异常类型");
        Assert(Directory.GetFiles(diagnostics, "diagnostic-*").Length <= 20,
            "CrashDiagnostic: 新写入后仍应最多保留 20 份");

        string debugRoot = Path.Combine(tempRoot, "diagnostic-debug-root");
        Directory.CreateDirectory(debugRoot);
        var debug = new CrashDiagnosticService(releaseMode: false, developmentRootOverride: debugRoot);
        debug.Initialize();
        string? debugPath = debug.WriteException(exception, "SELFTEST_DEBUG_CRASH");
        Assert(debugPath is not null
            && File.ReadAllText(debugPath).Contains(clipboardSentinel, StringComparison.Ordinal),
            "CrashDiagnostic: 合法 Debug 隔离根应保留完整诊断");

        var invalidDebug = new CrashDiagnosticService(
            releaseMode: false,
            developmentRootOverride: "relative-dev-root");
        Assert(invalidDebug.WriteException(exception, "INVALID_DEBUG_ROOT") is null,
            "CrashDiagnostic: 无合法 Debug 根时不得落完整日志");
    }

    // —— M4.4.2a 隐私自检 ——

    private static void RunPrivacyCapturePolicyTests()
    {
        Assert(PrivacyCapturePolicy.Decide(true, null, [], ClipboardExclusionState.Unknown, false)
            == PrivacyCaptureDecision.Skip, "PrivacyCapturePolicy: pause 应第一优先级跳过");
        Assert(PrivacyCapturePolicy.Decide(false, "notepad", [], ClipboardExclusionState.Excluded, false)
            == PrivacyCaptureDecision.Skip, "PrivacyCapturePolicy: 系统排除应跳过");
        Assert(PrivacyCapturePolicy.Decide(false, "notepad", [], ClipboardExclusionState.Unknown, false)
            == PrivacyCaptureDecision.Retry, "PrivacyCapturePolicy: 系统 Unknown 首次应重试");
        Assert(PrivacyCapturePolicy.Decide(false, "notepad", [], ClipboardExclusionState.Unknown, true)
            == PrivacyCaptureDecision.Skip, "PrivacyCapturePolicy: 系统 Unknown 重试后应 fail-closed");

        Assert(PrivacyCapturePolicy.Decide(false, null, [], ClipboardExclusionState.Allowed, false)
            == PrivacyCaptureDecision.Capture,
            "PrivacyCapturePolicy: 无应用排除名单时单纯进程 Unknown 不阻断");
        Assert(PrivacyCapturePolicy.Decide(false, null, ["notepad"], ClipboardExclusionState.Allowed, false)
            == PrivacyCaptureDecision.Retry,
            "PrivacyCapturePolicy: 有应用排除名单时进程 Unknown 首次应重试");
        Assert(PrivacyCapturePolicy.Decide(false, null, ["notepad"], ClipboardExclusionState.Allowed, true)
            == PrivacyCaptureDecision.Skip,
            "PrivacyCapturePolicy: 有应用排除名单时进程 Unknown 重试后应跳过");
        Assert(PrivacyCapturePolicy.Decide(false, "NOTEPAD", ["notepad"], ClipboardExclusionState.Allowed, false)
            == PrivacyCaptureDecision.Skip, "PrivacyCapturePolicy: 排除匹配应大小写不敏感");
        Assert(PrivacyCapturePolicy.Decide(false, "notepadplusplus", ["notepad"], ClipboardExclusionState.Allowed, false)
            == PrivacyCaptureDecision.Capture, "PrivacyCapturePolicy: 子串不应误匹配");
        Assert(PrivacyCapturePolicy.Decide(false, "chrome", ["notepad"], ClipboardExclusionState.Allowed, false)
            == PrivacyCaptureDecision.Capture, "PrivacyCapturePolicy: 已知且未命中应捕获");

        var retryState = new ClipboardPrivacyRetryState();
        retryState.Schedule(10);
        Assert(retryState.TryConsume(10), "PrivacyCapturePolicy: 同一序列号应允许一次重试");
        Assert(!retryState.TryConsume(10), "PrivacyCapturePolicy: 重试门闩只能消费一次");
        retryState.Schedule(20);
        Assert(!retryState.TryConsume(21), "PrivacyCapturePolicy: 序列号变化应放弃旧事件");
        retryState.Schedule(30);
        retryState.Cancel();
        Assert(!retryState.TryConsume(30), "PrivacyCapturePolicy: Stop/Dispose 取消后不得回调");
    }

    private static void RunClipboardExclusionMarkerTests()
    {
        // 仅 ExcludeClipboardContentFromMonitorProcessing → true
        Assert(ClipboardExclusionMarker.IsExcluded(hasExcludeFormat: true, hasHistoryFlag: false, historyFlagValue: 1),
            "ClipboardExclusionMarker: 仅 ExcludeClipboardContentFromMonitorProcessing 应排除");

        // CanIncludeInClipboardHistory=0 → true
        Assert(ClipboardExclusionMarker.IsExcluded(hasExcludeFormat: false, hasHistoryFlag: true, historyFlagValue: 0),
            "ClipboardExclusionMarker: CanIncludeInClipboardHistory=0 应排除");

        // CanIncludeInClipboardHistory=1 → false
        Assert(!ClipboardExclusionMarker.IsExcluded(hasExcludeFormat: false, hasHistoryFlag: true, historyFlagValue: 1),
            "ClipboardExclusionMarker: CanIncludeInClipboardHistory=1 不应排除");

        // 都无 → false
        Assert(!ClipboardExclusionMarker.IsExcluded(hasExcludeFormat: false, hasHistoryFlag: false, historyFlagValue: 0),
            "ClipboardExclusionMarker: 无任何排除格式应不排除");

        // 两者都有 → true（任一命中即排除）
        Assert(ClipboardExclusionMarker.IsExcluded(hasExcludeFormat: true, hasHistoryFlag: true, historyFlagValue: 1),
            "ClipboardExclusionMarker: 两个标记都有（但 history=1）应排除（Exclude 格式命中）");

        Assert(ClipboardExclusionMarker.Evaluate(false, true, 0, historyFlagValueKnown: false)
            == ClipboardExclusionState.Unknown,
            "ClipboardExclusionMarker: history 格式存在但值不可读应为 Unknown");
    }

    private static void RunRunningAppsProviderTests()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 正常应用
        bool ok1 = RunningAppsProvider.TryResolveAppInfo(
            processId: 100, mainWindowTitle: "Test Window", processName: "testapp",
            fileDescription: "Test Application", seen: seen,
            out RunningAppInfo info1);
        Assert(ok1, "RunApps: 正常应用应解析成功");
        Assert(info1.ProcessName == "testapp" && info1.DisplayName == "Test Application",
            "RunApps: 正常应用 DisplayName 应为 FileDescription");

        // 无 FileDescription → 回退 ProcessName
        bool ok2 = RunningAppsProvider.TryResolveAppInfo(
            processId: 200, mainWindowTitle: "Another", processName: "anotherapp",
            fileDescription: null, seen: seen,
            out RunningAppInfo info2);
        Assert(ok2, "RunApps: 无 FileDescription 应仍解析成功");
        Assert(info2.DisplayName == "anotherapp",
            $"RunApps: 无 FileDescription 时 DisplayName 应回退 ProcessName（实际='{info2.DisplayName}'）");

        // 无窗口标题 → 过滤
        bool ok3 = RunningAppsProvider.TryResolveAppInfo(
            processId: 300, mainWindowTitle: "", processName: "hiddenapp",
            fileDescription: "Hidden", seen: seen,
            out _);
        Assert(!ok3, "RunApps: 空窗口标题应过滤");

        // mainWindowTitle=null → 过滤
        bool ok4 = RunningAppsProvider.TryResolveAppInfo(
            processId: 400, mainWindowTitle: null, processName: "nulltitle",
            fileDescription: "Null", seen: seen,
            out _);
        Assert(!ok4, "RunApps: null 窗口标题应过滤");

        // 归一化去重
        var seen2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        seen2.Add("testapp"); // 已存在
        bool ok5 = RunningAppsProvider.TryResolveAppInfo(
            processId: 500, mainWindowTitle: "Dup", processName: "TESTAPP", // 大写
            fileDescription: "Duplicate", seen: seen2,
            out _);
        Assert(!ok5, "RunApps: 归一化名已存在应去重");

        // 排除 Clipora 自身
        bool ok6 = RunningAppsProvider.TryResolveAppInfo(
            processId: 600, mainWindowTitle: "Clipora", processName: "clipora",
            fileDescription: "Clipora App", seen: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            out _);
        Assert(!ok6, "RunApps: Clipora 自身应被排除");

        // Clipora 大小写不敏感
        bool ok7 = RunningAppsProvider.TryResolveAppInfo(
            processId: 700, mainWindowTitle: "CLIPORA", processName: "CLIPORA",
            fileDescription: "Clipora", seen: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            out _);
        Assert(!ok7, "RunApps: CLIPORA 大写自身应被排除");

        // DisplayName 空字符串 → 回退 ProcessName
        bool ok8 = RunningAppsProvider.TryResolveAppInfo(
            processId: 800, mainWindowTitle: "EmptyDesc", processName: "emptydesc",
            fileDescription: "", seen: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            out RunningAppInfo info8);
        Assert(ok8, "RunApps: 空 FileDescription 应仍解析成功");
        Assert(info8.DisplayName == "emptydesc",
            $"RunApps: 空 FileDescription 时 DisplayName 应回退 ProcessName（实际='{info8.DisplayName}'）");

        // 空白 FileDescription → 回退 ProcessName
        bool ok9 = RunningAppsProvider.TryResolveAppInfo(
            processId: 900, mainWindowTitle: "WhitespaceDesc", processName: "wspace",
            fileDescription: "   ", seen: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            out RunningAppInfo info9);
        Assert(ok9, "RunApps: 空白 FileDescription 应仍解析成功");
        Assert(info9.DisplayName == "wspace",
            $"RunApps: 空白 FileDescription 时 DisplayName 应回退 ProcessName（实际='{info9.DisplayName}'）");

        // 去重：大小写不敏感
        var seen3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RunningAppsProvider.TryResolveAppInfo(10, "Win1", "Unique", "Unique App", seen3, out _);
        bool ok10 = RunningAppsProvider.TryResolveAppInfo(20, "Win2", "UNIQUE", "Unique App 2", seen3, out _);
        Assert(!ok10, "RunApps: 归一化名大小写不敏感去重（UNIQUE vs Unique）");
    }

    private static void RunPrivacySettingsViewModelTests()
    {
        var settings = new SelfTestSettingsService();
        var autoStart = new AutoStartService("Clipora_SelfTest_Privacy");
        var svm = new SettingsViewModel(settings, autoStart, Path.GetTempPath());

        int saveBefore = settings.SaveCount;

        // Paused 即时保存
        svm.Paused = true;
        Assert(settings.Current.Paused && settings.SaveCount == saveBefore + 1,
            "PrivacySVM: Paused=true 应即时写盘");

        // Paused 关闭
        svm.Paused = false;
        Assert(!settings.Current.Paused && settings.SaveCount == saveBefore + 2,
            "PrivacySVM: Paused=false 应即时写盘");

        // ExcludedApps 默认空
        Assert(svm.ExcludedApps.Count == 0,
            "PrivacySVM: 新建 SettingsViewModel 时 ExcludedApps 应为空");

        // AddExclusion：归一化 + 持久化
        svm.AddExclusion("NotePad", "Notepad");
        Assert(svm.ExcludedApps.Count == 1,
            "PrivacySVM: AddExclusion 后 ExcludedApps.Count 应为 1");
        Assert(svm.ExcludedApps[0].ProcessName == "notepad",
            $"PrivacySVM: AddExclusion 应归一化 processName 为 lowercase（实际='{svm.ExcludedApps[0].ProcessName}'）");
        Assert(svm.ExcludedApps[0].DisplayName == "Notepad",
            "PrivacySVM: AddExclusion 应保留传入的 DisplayName");
        Assert(settings.Current.ExcludedApps.Count == 1 && settings.Current.ExcludedApps[0] == "notepad",
            "PrivacySVM: AddExclusion 应写入 settings.ExcludedApps");

        // AddExclusion 重复（同一进程名，不同大小写）→ 忽略
        int saveBeforeDup = settings.SaveCount;
        svm.AddExclusion("NOTEPAD", "Notepad++");
        Assert(svm.ExcludedApps.Count == 1,
            "PrivacySVM: AddExclusion 重复（大小写不敏感）不应增加条目");
        Assert(settings.SaveCount == saveBeforeDup,
            "PrivacySVM: AddExclusion 重复不应重复保存");

        // 添加第二个排除
        svm.AddExclusion("Chrome", "Google Chrome");
        Assert(svm.ExcludedApps.Count == 2 && settings.Current.ExcludedApps.Count == 2,
            "PrivacySVM: 第二个 AddExclusion 应增加条目");

        // RemoveExclusion
        svm.RemoveExclusion(svm.ExcludedApps[0]);
        Assert(svm.ExcludedApps.Count == 1,
            "PrivacySVM: RemoveExclusion 后 ExcludedApps.Count 应为 1");
        Assert(settings.Current.ExcludedApps.Count == 1,
            "PrivacySVM: RemoveExclusion 后 settings.ExcludedApps.Count 应为 1");
        Assert(settings.Current.ExcludedApps[0] == "chrome",
            $"PrivacySVM: RemoveExclusion 后剩 chrome（实际='{settings.Current.ExcludedApps[0]}'）");

        // RemoveExclusion 移除最后一项
        svm.RemoveExclusion(svm.ExcludedApps[0]);
        Assert(svm.ExcludedApps.Count == 0 && settings.Current.ExcludedApps.Count == 0,
            "PrivacySVM: 移除最后一项后名单应为空");

        // 清理自检注册表值
        autoStart.TrySetEnabled(false, out _);
    }

    // —— M4.5.2a 快捷键自检 ——

    private static void RunHotkeyGestureTests()
    {
        // TryParse 正常格式
        Assert(HotkeyGesture.TryParse("Ctrl+Shift+V", out var g1) && g1.IsValid,
            "HotkeyGesture: Ctrl+Shift+V 应解析成功");
        Assert(g1.Modifiers == (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT) && g1.VirtualKey == 0x56,
            $"HotkeyGesture: Ctrl+Shift+V mods/vk（实际 mods={g1.Modifiers} vk={g1.VirtualKey}）");

        // Format 往返
        Assert(g1.Format() == "Ctrl+Shift+V",
            $"HotkeyGesture: Ctrl+Shift+V Format 往返（实际='{g1.Format()}'）");

        // 规范序输出（Ctrl 总在最前）
        Assert(HotkeyGesture.TryParse("Shift+Ctrl+V", out var g2) && g2.Format() == "Ctrl+Shift+V",
            $"HotkeyGesture: Shift+Ctrl+V 应规范输出 Ctrl+Shift+V（实际='{g2.Format()}'）");

        // 大小写不敏感
        Assert(HotkeyGesture.TryParse("ctrl+alt+v", out var g3) && g3.Format() == "Ctrl+Alt+V",
            $"HotkeyGesture: ctrl+alt+v 大小写不敏感（实际='{g3.Format()}'）");

        // Win 键
        Assert(HotkeyGesture.TryParse("Win+V", out var g4) && g4.IsValid && g4.Modifiers == NativeMethods.MOD_WIN,
            "HotkeyGesture: Win+V 应解析成功");

        // Alt 单独
        Assert(HotkeyGesture.TryParse("Alt+F4", out var g5) && g5.Format() == "Alt+F4",
            $"HotkeyGesture: Alt+F4 解析（实际='{g5.Format()}'）");

        // F 键
        Assert(HotkeyGesture.TryParse("Ctrl+F12", out var g6) && g6.IsValid,
            "HotkeyGesture: Ctrl+F12 应解析成功");

        // 数字
        Assert(HotkeyGesture.TryParse("Ctrl+Shift+1", out var g7) && g7.IsValid && g7.VirtualKey == 0x31,
            "HotkeyGesture: Ctrl+Shift+1 应解析成功");

        // 空/空白 → 失败
        Assert(!HotkeyGesture.TryParse(null, out _), "HotkeyGesture: null 应解析失败");
        Assert(!HotkeyGesture.TryParse("", out _), "HotkeyGesture: 空串应解析失败");
        Assert(!HotkeyGesture.TryParse("  ", out _), "HotkeyGesture: 空白应解析失败");

        // 未知键 → 失败
        Assert(!HotkeyGesture.TryParse("Ctrl+F13", out _), "HotkeyGesture: Ctrl+F13（不存在）应解析失败");
        Assert(!HotkeyGesture.TryParse("Ctrl+Pause", out _), "HotkeyGesture: Ctrl+Pause（未知）应解析失败");

        // 缺修饰键 → TryParse 返回 false（IsValid=false）
        Assert(!HotkeyGesture.TryParse("V", out _),
            "HotkeyGesture: V（无修饰键）应解析失败");

        // 多个主键 → 失败
        Assert(!HotkeyGesture.TryParse("Ctrl+V+X", out _), "HotkeyGesture: Ctrl+V+X（多主键）应解析失败");

        // 纯修饰键 → 失败（无主键）
        Assert(!HotkeyGesture.TryParse("Ctrl+Shift", out _),
            "HotkeyGesture: Ctrl+Shift（仅修饰键）应解析失败");

        // FromKeyInput
        var fki = HotkeyGesture.FromKeyInput(0x56, ctrl: true, alt: false, shift: true, win: false);
        Assert(fki.Modifiers == (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT) && fki.VirtualKey == 0x56,
            "HotkeyGesture: FromKeyInput(V, ctrl, shift) 应正确构造");

        // Format 对无效 gesture → 空串
        var invalid = new HotkeyGesture(0, 0x56);
        Assert(invalid.Format() == string.Empty, "HotkeyGesture: 无效 gesture Format 应返回空串");
    }

    private static void RunHotkeyConflictCheckerTests()
    {
        // 两动作同 gesture → 均冲突
        var g = new HotkeyGesture(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x56); // Ctrl+Alt+V
        var dict1 = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.OpenPanel] = g,
            [HotkeyAction.PastePlain] = g,
        };
        var dup1 = HotkeyConflictChecker.FindDuplicates(dict1);
        Assert(dup1.Count == 2 && dup1.Contains(HotkeyAction.OpenPanel) && dup1.Contains(HotkeyAction.PastePlain),
            "HotkeyConflictChecker: 两动作同 gesture 应均报告冲突");

        // 全部不同 → 空
        var dict2 = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.OpenPanel] = new HotkeyGesture(NativeMethods.MOD_ALT, 0x56),      // Alt+V
            [HotkeyAction.PastePlain] = new HotkeyGesture(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x56), // Ctrl+Shift+V
        };
        var dup2 = HotkeyConflictChecker.FindDuplicates(dict2);
        Assert(dup2.Count == 0, "HotkeyConflictChecker: 全部不同应返回空");

        // 含无效 gesture 跳过
        var dict3 = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.OpenPanel] = g,
            [HotkeyAction.PastePlain] = g,
            [HotkeyAction.SequentialPaste] = new HotkeyGesture(0, 0), // 无效，不参与冲突
        };
        var dup3 = HotkeyConflictChecker.FindDuplicates(dict3);
        Assert(dup3.Count == 2, "HotkeyConflictChecker: 无效 gesture 应跳过，不报告为冲突方");

        // 三动作中两组冲突
        var dict4 = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.OpenPanel] = new HotkeyGesture(NativeMethods.MOD_ALT, 0x56),
            [HotkeyAction.PastePlain] = new HotkeyGesture(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x56),
            [HotkeyAction.SequentialPaste] = new HotkeyGesture(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x56),
        };
        var dup4 = HotkeyConflictChecker.FindDuplicates(dict4);
        Assert(dup4.Count == 2 && dup4.Contains(HotkeyAction.PastePlain) && dup4.Contains(HotkeyAction.SequentialPaste),
            "HotkeyConflictChecker: 三动作中 PastePlain/Seq 冲突应只报告两者");

        // 空字典
        var dup5 = HotkeyConflictChecker.FindDuplicates(new Dictionary<HotkeyAction, HotkeyGesture>());
        Assert(dup5.Count == 0, "HotkeyConflictChecker: 空字典应返回空");
    }

    // —— M4.5-seq 顺序粘贴自检 ——

    private static void RunSequentialPasteBurstPlannerTests()
    {
        double gap = 30;

        // 单条 → [0]（oldest=0, newest=0）
        var single = ComputeBurst(new[] { T(0) }, gap);
        Assert(single.Count == 1 && single[0] == 0,
            "BurstPlanner: 单条应返回 [0]");

        // 全部间隔内 → 全集 oldest→newest（降序: newest=T(0)…oldest=T(60)）
        var all = ComputeBurst(new[] { T(0), T(15), T(30), T(45), T(60) }, gap);
        Assert(all.Count == 5 && all[0] == 4 && all[4] == 0,
            $"BurstPlanner: 全部间隔内应为 [4,3,2,1,0]（实际 Count={all.Count} [0]={all[0]} [4]={all[4]}）");

        // 中间断开 → 只取最新段（降序: T(40), T(50), T(90), T(100)；gap(40→50=10 ≤30, 50→90=40 >30 break)）
        var split = ComputeBurst(new[] { T(40), T(50), T(90), T(100) }, gap);
        Assert(split.Count == 2 && split[0] == 1 && split[1] == 0,
            $"BurstPlanner: 中间断开应只取最新段（实际 Count={split.Count} [0]={split[0]} [1]={split[1]}）");

        // 空输入 → 空
        var empty = SequentialPasteBurstPlanner.ComputeMostRecentBurst(Array.Empty<DateTime>(), gap);
        Assert(empty.Count == 0, "BurstPlanner: 空输入应返回空");

        // 全部间隔都超过阈值 → 只取最新一条（降序: T(0), T(100), T(200); gap(0→100=100>30 break)）
        var only = ComputeBurst(new[] { T(0), T(100), T(200) }, gap);
        Assert(only.Count == 1 && only[0] == 0,
            $"BurstPlanner: 全部间隔超阈值应只取最新一条（实际 Count={only.Count}）");

        // MaxBurstScan 截断：列表中 >50 条但只传 50 条进来，自然被截断。
        // （自检不模拟 >50，因为函数本身不硬编码 limit，只处理传入列表）
    }

    private static void RunSequentialPasteSessionTests()
    {
        var session = new SequentialPasteSession(idleSeconds: 60);
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

        // burst = [103, 102, 101, 100]（oldest→newest，值为稳定 ClipItem Id）
        var burst = new long[] { 103, 102, 101, 100 };

        // 首次按键 → 粘 burst[0]=103（最旧）
        var s1 = session.Press(burst, now);
        Assert(s1 is { ItemId: 103, IsFirstOfSession: true },
            $"SeqSession: 首次按键应返回最旧项 id=103, IsFirst=true（实际 id={s1?.ItemId} first={s1?.IsFirstOfSession}）");

        // 后续推进
        var s2 = session.Press(burst, now.AddSeconds(2));
        Assert(s2 is { ItemId: 102, IsFirstOfSession: false },
            $"SeqSession: 第二次应返回 id=102（实际 id={s2?.ItemId}）");

        // 推进到最新
        session.Press(burst, now.AddSeconds(4)); // 101
        var s4 = session.Press(burst, now.AddSeconds(6)); // 100（最新），完成
        Assert(s4 is { ItemId: 100, IsFirstOfSession: false },
            $"SeqSession: 第四次应返回 id=100（实际 id={s4?.ItemId}）");

        // 完成后同一批次保持耗尽，不得从头循环
        var s5 = session.Press(burst, now.AddSeconds(8));
        Assert(s5 is null,
            $"SeqSession: 完成后继续按键应 no-op（实际 id={s5?.ItemId}）");
        var s5Late = session.Press(burst, now.AddMinutes(10));
        Assert(s5Late is null,
            $"SeqSession: 已完成批次即使超过 idle 也不得循环（实际 id={s5Late?.ItemId}）");

        // 完成后 burst 变化可开启新批次
        var afterCompletedBurst = new long[] { 205, 204 };
        var sChangedAfterComplete = session.Press(afterCompletedBurst, now.AddMinutes(10).AddSeconds(1));
        Assert(sChangedAfterComplete is { ItemId: 205, IsFirstOfSession: true },
            $"SeqSession: 完成后 burst 变化应开启新批次（实际 id={sChangedAfterComplete?.ItemId}）");

        // idle 超时重开
        session.Reset();
        session.Press(burst, now.AddSeconds(10));
        var s7 = session.Press(burst, now.AddSeconds(71)); // >60s idle
        Assert(s7 is { ItemId: 103, IsFirstOfSession: true },
            $"SeqSession: idle 超时应重新开批（实际 id={s7?.ItemId} first={s7?.IsFirstOfSession}）");

        // Reset() 后重开
        session.Reset();
        var s8 = session.Press(burst, now.AddSeconds(1));
        Assert(s8 is { ItemId: 103, IsFirstOfSession: true },
            $"SeqSession: Reset() 后应重新开批（实际 id={s8?.ItemId} first={s8?.IsFirstOfSession}）");

        // burst 变化重开（新 burst 内容不同）
        var newBurst = new long[] { 205, 204 };
        session.Press(burst, now); // 103
        var s9 = session.Press(newBurst, now.AddSeconds(1)); // burst 变了 → 重开，粘 205
        Assert(s9 is { ItemId: 205, IsFirstOfSession: true },
            $"SeqSession: burst 变化应重新开批（实际 id={s9?.ItemId} first={s9?.IsFirstOfSession}）");

        // 同长度、同首尾但中间项变化也必须重开（回归：近似哈希会漏检）。
        var collisionShape = new long[] { 103, 999, 101, 100 };
        session.Reset();
        session.Press(burst, now);
        var sCollision = session.Press(collisionShape, now.AddSeconds(1));
        Assert(sCollision is { ItemId: 103, IsFirstOfSession: true },
            $"SeqSession: 批次中间 Id 变化应重新开批（实际 id={sCollision?.ItemId} first={sCollision?.IsFirstOfSession}）");

        // 空 burst → null
        var s10 = session.Press(Array.Empty<long>(), now);
        Assert(s10 is null,
            $"SeqSession: 空 burst 应返回 null（实际={s10}）");

        // 单条 burst 首次粘贴后立即耗尽
        var singleSession = new SequentialPasteSession(idleSeconds: 60);
        var singleBurst = new long[] { 301 };
        Assert(singleSession.Press(singleBurst, now) is { ItemId: 301 },
            "SeqSession: 单条 burst 首次应粘贴该项");
        Assert(singleSession.Press(singleBurst, now.AddSeconds(1)) is null,
            "SeqSession: 单条 burst 第二次按键应 no-op");
    }

    private static void RunSequentialPasteStoreQueryTest(string root)
    {
        string queryRoot = Path.Combine(root, "sequential-query");
        var store = new SqliteClipStore(new Database(new AppPaths(queryRoot)));
        DateTime now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 101; i++)
        {
            long pinnedId = store.Add(new ClipItem
            {
                Type = ClipType.Text,
                PreviewText = $"old-pinned-{i}",
                TextContent = $"old-pinned-{i}",
                ContentHash = $"old-pinned-{i}",
                CreatedAt = now.AddDays(-2).AddSeconds(i),
            }, mergeDuplicates: false);
            store.SetPinned(pinnedId, true);
        }

        for (int i = 0; i < 3; i++)
        {
            store.Add(new ClipItem
            {
                Type = ClipType.Text,
                PreviewText = $"recent-{i}",
                TextContent = $"recent-{i}",
                ContentHash = $"recent-{i}",
                CreatedAt = now.AddSeconds(i),
            }, mergeDuplicates: false);
        }

        IReadOnlyList<ClipItem> recent = store.Query(new ClipQuery
        {
            Take = 3,
            PrioritizePinned = false,
        });
        Assert(recent.Count == 3
            && recent[0].PreviewText == "recent-2"
            && recent[1].PreviewText == "recent-1"
            && recent[2].PreviewText == "recent-0",
            "SeqStoreQuery: 超过 100 条旧置顶项时仍应按 CreatedAt 取到最新非置顶项");
    }

    private static void RunHotkeyPasteReleaseGateTests()
    {
        const uint mainKey = 0x4D; // M
        var down = new HashSet<int>();
        bool IsDown(int key) => down.Contains(key);

        Assert(HotkeyPasteReleaseGate.AreReleased(mainKey, IsDown),
            "HotkeyPasteReleaseGate: 无按键按下时应允许粘贴");

        down.Add((int)mainKey);
        Assert(!HotkeyPasteReleaseGate.AreReleased(mainKey, IsDown),
            "HotkeyPasteReleaseGate: 主键未释放时必须等待");
        down.Clear();

        foreach (int modifier in new[]
        {
            NativeMethods.VK_CONTROL,
            NativeMethods.VK_SHIFT,
            NativeMethods.VK_MENU,
            NativeMethods.VK_LWIN,
            NativeMethods.VK_RWIN,
        })
        {
            down.Add(modifier);
            Assert(!HotkeyPasteReleaseGate.AreReleased(mainKey, IsDown),
                $"HotkeyPasteReleaseGate: 修饰键 0x{modifier:X2} 未释放时必须等待");
            down.Clear();
        }
    }

    private static void RunTrayStartupPolicyTests()
    {
        Assert(TrayStartupPolicy.Decide(TrayCreateResult.Success) == TrayStartupDecision.ContinueNormal,
            "TrayStartupPolicy: 托盘创建成功 → 按 SilentStart 正常决定");
        Assert(TrayStartupPolicy.Decide(TrayCreateResult.Failure) == TrayStartupDecision.ForceShowPanel,
            "TrayStartupPolicy: 托盘创建失败 → 必须强制显示主面板");
    }

    /// <summary>TimeFormat 时间显示：今天/昨天/今年/往年边界。</summary>
    private static void RunTimeFormatTests()
    {
        DateTime utcNow = DateTime.UtcNow;

        // —— Display() ——
        // 今天
        string todayDisplay = TimeFormat.Display(utcNow);
        Assert(todayDisplay.StartsWith("今天 ") && todayDisplay.Length == "今天 HH:mm".Length,
            $"TimeFormat.Display: 今天应输出'今天 HH:mm'（实际='{todayDisplay}'）");

        // 昨天
        string yesterdayDisplay = TimeFormat.Display(utcNow.AddDays(-1));
        Assert(yesterdayDisplay.StartsWith("昨天 ") && yesterdayDisplay.Length == "昨天 HH:mm".Length,
            $"TimeFormat.Display: 昨天应输出'昨天 HH:mm'（实际='{yesterdayDisplay}'）");

        // 往年（2020 年）→ "yyyy-MM-dd"
        DateTime pastYear = new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        string pastYearDisplay = TimeFormat.Display(pastYear);
        Assert(pastYearDisplay == "2020-06-15",
            $"TimeFormat.Display: 往年应输出 yyyy-MM-dd（实际='{pastYearDisplay}'）");

        // 今年更早（3 天前，非今天/昨天）→ "MM-dd"
        DateTime threeDaysAgo = utcNow.AddDays(-3);
        string threeDaysDisplay = TimeFormat.Display(threeDaysAgo);
        // 若 3 天前仍在今年，格式为 MM-dd；若跨年，格式为 yyyy-MM-dd（均非"今天"/"昨天"即可）
        Assert(!threeDaysDisplay.StartsWith("今天") && !threeDaysDisplay.StartsWith("昨天"),
            $"TimeFormat.Display: 3 天前不应输出今天/昨天（实际='{threeDaysDisplay}'）");

        // —— DayGroup() ——
        string todayGroup = TimeFormat.DayGroup(utcNow);
        Assert(todayGroup == "今天",
            $"TimeFormat.DayGroup: 今天应输出'今天'（实际='{todayGroup}'）");

        string yesterdayGroup = TimeFormat.DayGroup(utcNow.AddDays(-1));
        Assert(yesterdayGroup == "昨天",
            $"TimeFormat.DayGroup: 昨天应输出'昨天'（实际='{yesterdayGroup}'）");

        string pastYearGroup = TimeFormat.DayGroup(pastYear);
        Assert(pastYearGroup == "2020年6月15日",
            $"TimeFormat.DayGroup: 往年应输出'yyyy年M月d日'（实际='{pastYearGroup}'）");

        string threeDaysGroup = TimeFormat.DayGroup(threeDaysAgo);
        Assert(!threeDaysGroup.StartsWith("今天") && !threeDaysGroup.StartsWith("昨天"),
            $"TimeFormat.DayGroup: 3 天前不应输出今天/昨天（实际='{threeDaysGroup}'）");
    }

    /// <summary>ClipboardInternalWriteMarker 标记逻辑：SetData→IsPresent 往返。</summary>
    private static void RunClipboardInternalWriteMarkerTests()
    {
        // SetData + IsPresent 往返
        var dataObject = new DataObject();
        Assert(!ClipboardInternalWriteMarker.IsPresent(dataObject),
            "IWM: 未设标记时 IsPresent 应为 false");

        dataObject.SetData(ClipboardInternalWriteMarker.Format, true, autoConvert: false);
        Assert(ClipboardInternalWriteMarker.IsPresent(dataObject),
            "IWM: SetData 后 IsPresent 应为 true");

        // 格式名常量非空
        Assert(!string.IsNullOrWhiteSpace(ClipboardInternalWriteMarker.Format),
            "IWM: Format 常量不应为空");

        // 标记后 DataObject 仍可正常携带其他格式
        var multiFormat = new DataObject();
        multiFormat.SetText("hello", TextDataFormat.UnicodeText);
        multiFormat.SetData(ClipboardInternalWriteMarker.Format, true, autoConvert: false);
        Assert(ClipboardInternalWriteMarker.IsPresent(multiFormat),
            "IWM: 含文本+标记时 IsPresent 应为 true");
        Assert(multiFormat.GetDataPresent(DataFormats.UnicodeText, autoConvert: false),
            "IWM: 含文本+标记时文本格式应保留");
    }

    /// <summary>辅助：用秒数偏移构建 DateTime 列表（0=最新，值越大=越旧）。</summary>
    private static DateTime T(double secondsAgo) =>
        new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc).AddSeconds(-secondsAgo);

    private static IReadOnlyList<int> ComputeBurst(DateTime[] descending, double gap) =>
        SequentialPasteBurstPlanner.ComputeMostRecentBurst(descending, gap);

    // —— OCR 存储层纯函数自检 ——

    private static void RunOcrStoreTests(SqliteClipStore clips, SelfTestSettingsService settingsStub)
    {
        // 1. MarkLegacyImagesPending: 将 Type=Image 且 OcrStatus=None 的项标记为 Pending
        long img1 = clips.Add(new ClipItem
        {
            Type = ClipType.Image,
            PreviewText = "test-image-1",
            ContentHash = "ocr-test-hash-1",
            CreatedAt = DateTime.UtcNow,
            OcrStatus = OcrStatus.None,
        }, mergeDuplicates: false);
        long img2 = clips.Add(new ClipItem
        {
            Type = ClipType.Image,
            PreviewText = "test-image-2",
            ContentHash = "ocr-test-hash-2",
            CreatedAt = DateTime.UtcNow,
            OcrStatus = OcrStatus.None,
        }, mergeDuplicates: false);

        // 插入一条非图片项（不应被标记）
        clips.Add(new ClipItem
        {
            Type = ClipType.Text,
            PreviewText = "not-an-image",
            ContentHash = "ocr-test-text",
            CreatedAt = DateTime.UtcNow,
        }, mergeDuplicates: false);

        // 插入一条已完成 OCR 的图片（不应被标记）
        clips.Add(new ClipItem
        {
            Type = ClipType.Image,
            PreviewText = "done-image",
            ContentHash = "ocr-test-done",
            CreatedAt = DateTime.UtcNow,
            OcrStatus = OcrStatus.Completed,
        }, mergeDuplicates: false);

        // 执行回填标记
        clips.MarkLegacyImagesPending();

        // 断言：两 None 图片应变为 Pending
        ClipItem? img1After = clips.GetById(img1);
        Assert(img1After is not null, "OCR-1: img1 应存在");
        Assert(img1After!.OcrStatus == OcrStatus.Pending, "OCR-1: MarkLegacyImagesPending 应将 None 改为 Pending");

        ClipItem? img2After = clips.GetById(img2);
        Assert(img2After is not null, "OCR-2: img2 应存在");
        Assert(img2After!.OcrStatus == OcrStatus.Pending, "OCR-2: MarkLegacyImagesPending 应将 None 改为 Pending");

        // 断言：非图片项不受影响
        var textItems = clips.Query(new ClipQuery { Type = ClipType.Text });
        Assert(textItems.Count >= 1, "OCR-3: 文本项应仍存在");
        Assert(textItems[0].OcrStatus == OcrStatus.None, "OCR-3: 非图片项 OcrStatus 应保持 None");

        // 断言：已完成图片不受影响
        var doneItems = clips.Query(new ClipQuery { Type = ClipType.Image, Search = "done-image" });
        Assert(doneItems.Count >= 1, "OCR-4: 已完成图片应仍存在");
        Assert(doneItems[0].OcrStatus == OcrStatus.Completed, "OCR-4: 已完成图片不应被改为 Pending");

        // 2. ListPendingOcr: 只返回 Pending 的图片
        var pending = clips.ListPendingOcr(10);
        Assert(pending.Count >= 2, "OCR-5: ListPendingOcr 应至少返回 2 条 Pending 项");
        foreach (var p in pending)
        {
            Assert(p.Type == ClipType.Image, "OCR-5: 所有 Pending 项应是 Image");
            Assert(p.OcrStatus == OcrStatus.Pending, "OCR-5: 所有 Pending 项状态应为 Pending");
        }

        // 3. SetOcrResult: 正确回写状态与文本
        clips.SetOcrResult(img1, OcrOutcome.Recognized, "识别出的中文文字");
        ClipItem? updated = clips.GetById(img1);
        Assert(updated is not null, "OCR-6: 更新后项应存在");
        Assert(updated!.OcrStatus == OcrStatus.Completed, "OCR-6: Recognized→Completed");
        Assert(updated.OcrText == "识别出的中文文字", "OCR-6: OcrText 应回写");

        // Empty 识别
        clips.SetOcrResult(img2, OcrOutcome.Empty, null);
        ClipItem? emptyItem = clips.GetById(img2);
        Assert(emptyItem is not null, "OCR-7: Empty 项应存在");
        Assert(emptyItem!.OcrStatus == OcrStatus.Empty, "OCR-7: Empty→Empty");
        Assert(emptyItem.OcrText is null, "OCR-7: Empty 的 OcrText 应为 null");

        // Failed 识别
        long failImg = clips.Add(new ClipItem
        {
            Type = ClipType.Image,
            PreviewText = "will-fail",
            ContentHash = "ocr-test-fail",
            CreatedAt = DateTime.UtcNow,
            OcrStatus = OcrStatus.Pending,
        }, mergeDuplicates: false);
        clips.SetOcrResult(failImg, OcrOutcome.Failed, null);
        ClipItem? failedItem = clips.GetById(failImg);
        Assert(failedItem is not null, "OCR-8: Failed 项应存在");
        Assert(failedItem!.OcrStatus == OcrStatus.Failed, "OCR-8: Failed→Failed");

        // 4. 搜索命中 OcrText
        clips.SetOcrResult(img1, OcrOutcome.Recognized, "一段关于人工智能的图片文字");
        var searchResult = clips.Query(new ClipQuery { Search = "人工智能" });
        Assert(searchResult.Count >= 1, "OCR-9: 搜索'人工智能'应命中 OcrText");
        Assert(searchResult[0].Id == img1, "OCR-9: 命中项应为 img1");

        // 搜索不命中无关文字
        var noResult = clips.Query(new ClipQuery { Search = "不存在的关键词XYZABC" });
        Assert(noResult.Count == 0, "OCR-10: 无关搜索应返回空");

        // CJK 去空格搜索：OCR 输出"人 工 智 能"应能被"人工智能"（无空格）命中
        clips.SetOcrResult(img1, OcrOutcome.Recognized, "人 工 智 能 与 机 器 学 习");
        var cjkResult = clips.Query(new ClipQuery { Search = "人工智能" });
        Assert(cjkResult.Count >= 1, "OCR-10b: 中文去空格搜索'人工智能'应命中'人 工 智 能'");

        // 5. OcrBackfillCompleted 设置后持久化
        Assert(!settingsStub.Current.OcrBackfillCompleted, "OCR-11: 初始回填标记应为 false");
        settingsStub.Current.OcrBackfillCompleted = true;
        settingsStub.Save();
        Assert(settingsStub.Current.OcrBackfillCompleted, "OCR-11: 设为 true 后应持久化");
    }

    // —— 备份导入导出纯函数自检 ——

    private static void RunDatabaseRecoveryTests(string dir)
    {
        string recRoot = Path.Combine(dir, "dbrecovery-test-root");
        Directory.CreateDirectory(recRoot);
        var paths = new AppPaths(recRoot, new StorageLocationService(new MemoryStorageRootLocator(recRoot)));
        string dbPath = paths.DbPath;

        // 1. 健康库不得被判损坏 / 触发恢复（防误删用户数据）
        var healthyDb = new Database(paths); // 建表
        healthyDb.Open().Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert(DatabaseRecoveryService.IsHealthyForTest(dbPath), "DBR-1: 健康库应判定为健康");
        var noRecovery = new DatabaseRecoveryService(paths).EnsureHealthy();
        Assert(!noRecovery.Recovered, "DBR-1: 健康库不应触发恢复");
        Assert(File.Exists(dbPath), "DBR-1: 健康库文件应原位保留");

        // 2. 损坏库应被检测 → 备份 → 腾出原位置
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string side in new[] { dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(side)) File.Delete(side);
        byte[] corruptBytes = Encoding.ASCII.GetBytes("this is definitely not a valid sqlite database file payload 0123456789");
        File.WriteAllBytes(dbPath, corruptBytes);
        Assert(!DatabaseRecoveryService.IsHealthyForTest(dbPath), "DBR-2: 损坏库应判定为不健康");

        var recovery = new DatabaseRecoveryService(paths).EnsureHealthy();
        Assert(recovery.Recovered, "DBR-2: 损坏库应触发恢复");
        Assert(recovery.CorruptPath == dbPath, "DBR-2: CorruptPath 应为原 db 路径");
        Assert(recovery.NewPath == dbPath, "DBR-2: NewPath 应为原 db 路径（同位置重建）");
        Assert(recovery.BackupPath is not null && File.Exists(recovery.BackupPath),
            "DBR-2: 备份文件应存在");
        Assert(!File.Exists(dbPath), "DBR-2: 原位置损坏文件应已移走（待重建）");
        byte[] backedUp = File.ReadAllBytes(recovery.BackupPath!);
        Assert(backedUp.Length == corruptBytes.Length, "DBR-2: 备份内容长度应与损坏库一致");

        // 3. 重建后应为可用空库
        var rebuilt = new Database(paths);
        var store = new SqliteClipStore(rebuilt);
        var items = store.Query(new ClipQuery { Take = 10 });
        Assert(items.Count == 0, "DBR-3: 重建后应为空库且可正常查询");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert(DatabaseRecoveryService.IsHealthyForTest(dbPath), "DBR-3: 重建库应判定为健康");
    }

    private static void RunBackupTests(string dir, Database database, SqliteClipStore clips, SettingsService settingsService)
    {
        // 隔离数据库：创建独立子目录用于备份测试
        string backupDir = Path.Combine(dir, "backup-test-root");
        Directory.CreateDirectory(backupDir);
        var paths = new AppPaths(backupDir, new StorageLocationService(new MemoryStorageRootLocator(backupDir)));
        var backupDb = new Database(paths);
        var backupClips = new SqliteClipStore(backupDb);
        var backupService = new BackupService(paths, backupDb);

        // 插入 3 条测试数据
        backupClips.Add(new ClipItem
        {
            Type = ClipType.Text, PreviewText = "backup-test-alpha", TextContent = "alpha content",
            ContentHash = "backup-hash-alpha", CreatedAt = DateTime.UtcNow,
        }, mergeDuplicates: false);
        backupClips.Add(new ClipItem
        {
            Type = ClipType.Url, PreviewText = "https://example.com", TextContent = "https://example.com",
            ContentHash = "backup-hash-url", CreatedAt = DateTime.UtcNow,
        }, mergeDuplicates: false);
        backupClips.Add(new ClipItem
        {
            Type = ClipType.Text, PreviewText = "backup-test-beta", TextContent = "beta content",
            ContentHash = "backup-hash-beta", CreatedAt = DateTime.UtcNow,
        }, mergeDuplicates: false);

        // 第 4 条：带受管 payload 的图片项（验证跨根 payload 重链，契约 §3.84）
        string srcImgPath = Path.Combine(paths.ImagesDir, "backup-img.png");
        string srcThumbPath = Path.Combine(paths.ThumbsDir, "backup-thumb.png");
        File.WriteAllBytes(srcImgPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        File.WriteAllBytes(srcThumbPath, new byte[] { 9, 10, 11, 12 });
        backupClips.Add(new ClipItem
        {
            Type = ClipType.Image, PreviewText = "backup-test-image",
            RefPath = srcImgPath, ThumbnailPath = srcThumbPath,
            ContentHash = "backup-hash-image", CreatedAt = DateTime.UtcNow,
        }, mergeDuplicates: false);

        // 1. 导出测试
        string archivePath = Path.Combine(backupDir, "test-backup.clpbak");
        var exportResult = backupService.ExportAsync(archivePath, null, CancellationToken.None).Result;
        Assert(exportResult.Ok, "BKP-1: 导出应成功");
        Assert(exportResult.ItemCount == 4, $"BKP-1: 导出应有 4 条记录（实际={exportResult.ItemCount}）");
        Assert(File.Exists(archivePath), "BKP-1: .clpbak 文件应存在");
        Assert(exportResult.Bytes > 0, "BKP-1: 文件大小应 > 0");

        // 2. InspectAsync 预检
        var preview = backupService.InspectAsync(archivePath, CancellationToken.None).Result;
        Assert(preview.Compatible, "BKP-2: 归档应兼容");
        Assert(preview.ItemCount == 4, $"BKP-2: 应报告 4 条（实际={preview.ItemCount}）");

        // 3. 导入到新目录（模拟恢复）
        string restoreDir = Path.Combine(dir, "restore-root");
        Directory.CreateDirectory(restoreDir);
        var restorePaths = new AppPaths(restoreDir, new StorageLocationService(new MemoryStorageRootLocator(restoreDir)));
        var restoreDb = new Database(restorePaths);
        var restoreClips = new SqliteClipStore(restoreDb);
        var restoreBackup = new BackupService(restorePaths, restoreDb);

        var importResult = restoreBackup.ImportAsync(archivePath, null, CancellationToken.None).Result;
        Assert(importResult.Ok, $"BKP-3: 导入应成功（错误={importResult.Error}）");
        Assert(importResult.Imported == 4, $"BKP-3: 应导入 4 条（实际={importResult.Imported}）");
        Assert(importResult.Skipped == 0, $"BKP-3: 应跳过 0 条（实际={importResult.Skipped}）");

        // 验证导入的数据
        var imported = restoreClips.Query(new ClipQuery { Take = 100 });
        Assert(imported.Count == 4, $"BKP-3: 恢复库应有 4 条（实际={imported.Count}）");

        // 3b. 跨根 payload 重链：图片项的路径列必须指向恢复根下实际落地的文件（契约 §3.84）
        var importedImage = imported.FirstOrDefault(c => c.ContentHash == "backup-hash-image");
        Assert(importedImage is not null, "BKP-3b: 图片项应被导入");
        string restoreFullRoot = Path.GetFullPath(restoreDir);
        Assert(importedImage!.RefPath is not null
            && Path.GetFullPath(importedImage.RefPath).StartsWith(restoreFullRoot, StringComparison.OrdinalIgnoreCase),
            $"BKP-3b: 图片 RefPath 应重链到恢复根（实际={importedImage.RefPath}）");
        Assert(File.Exists(importedImage.RefPath!), "BKP-3b: 重链后的原图文件应存在");
        Assert(importedImage.ThumbnailPath is not null
            && Path.GetFullPath(importedImage.ThumbnailPath).StartsWith(restoreFullRoot, StringComparison.OrdinalIgnoreCase),
            $"BKP-3b: 缩略图路径应重链到恢复根（实际={importedImage.ThumbnailPath}）");
        Assert(File.Exists(importedImage.ThumbnailPath!), "BKP-3b: 重链后的缩略图文件应存在");

        // 4. 去重导入：再次导入相同归档，应全部跳过
        var reimportResult = restoreBackup.ImportAsync(archivePath, null, CancellationToken.None).Result;
        Assert(reimportResult.Ok && reimportResult.Skipped == 4,
            $"BKP-4: 重复导入应全部跳过（实际导入={reimportResult.Imported}，跳过={reimportResult.Skipped}）");

        // 5. Zip Slip 拒绝
        Assert(BackupService.IsZipSlipForTest("../evil.exe"), "BKP-5: ../ 应被识别为 ZipSlip");
        Assert(BackupService.IsZipSlipForTest("..\\evil.exe"), "BKP-5: ..\\ 应被识别为 ZipSlip");
        Assert(BackupService.IsZipSlipForTest("foo/../../bar"), "BKP-5: 嵌套 ../ 应被识别为 ZipSlip");
        Assert(!BackupService.IsZipSlipForTest("payloads/images/good.png"), "BKP-5: 正常路径不应触发");
        // 绝对/带盘符/UNC/驱动器相对路径必须拒绝（Path.Combine 丢弃 staging 前缀的逃逸面）
        Assert(BackupService.IsZipSlipForTest("C:\\Windows\\evil.exe"), "BKP-5: 盘符绝对路径应被识别为 ZipSlip");
        Assert(BackupService.IsZipSlipForTest("/etc/passwd"), "BKP-5: 根路径应被识别为 ZipSlip");
        Assert(BackupService.IsZipSlipForTest("\\\\server\\share\\x"), "BKP-5: UNC 路径应被识别为 ZipSlip");
        Assert(BackupService.IsZipSlipForTest("C:evil.exe"), "BKP-5: 驱动器相对路径应被识别为 ZipSlip");
        // 重链映射：归档相对路径 → 实际落地，按后缀匹配（异机绝对路径前缀无关）
        var relMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["images\\foo.png"] = "D:\\NewRoot\\images\\foo.png",
        };
        Assert(BackupService.RelinkPayloadPath("C:\\OldRoot\\images\\foo.png", relMap) == "D:\\NewRoot\\images\\foo.png",
            "BKP-3c: 异机绝对路径应重链到 finalPath");
        Assert(BackupService.RelinkPayloadPath("C:\\OldRoot\\images\\other.png", relMap) == "C:\\OldRoot\\images\\other.png",
            "BKP-3c: 未在归档内的外部引用应保持原值");

        // 6. 崩溃恢复：sentinel 存在 → 保留
        var recoveryDir = Path.Combine(dir, "recovery-test");
        Directory.CreateDirectory(recoveryDir);
        var recoveryPaths = new AppPaths(recoveryDir, new StorageLocationService(new MemoryStorageRootLocator(recoveryDir)));
        var recoveryDb = new Database(recoveryPaths);
        Guid testId = Guid.NewGuid();

        // 模拟已提交：写入 sentinel + journal
        string stagingPath = Path.Combine(recoveryPaths.Root, ".backup-import-staging", testId.ToString("D"));
        Directory.CreateDirectory(stagingPath);
        var journal = new BackupImportJournal
        {
            ImportId = testId.ToString("D"),
            StagingRoot = stagingPath,
            FinalPaths = new List<string> { Path.Combine(recoveryPaths.Root, "images", "test-recovery.png") },
            Phase = "pre_commit",
        };
        File.WriteAllText(Path.Combine(stagingPath, "import-journal.json"),
            System.Text.Json.JsonSerializer.Serialize(journal));
        // 写哨兵
        using (var c = recoveryDb.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO backup_import_batches (ImportId,CommittedAtUtc) VALUES ($id,$t);";
            cmd.Parameters.AddWithValue("$id", testId.ToString("D"));
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        var recovery = new BackupImportRecoveryService(recoveryDb, recoveryPaths.Root);
        recovery.RecoverAll();

        // sentinel 存在 → staging 应被清理
        Assert(!Directory.Exists(stagingPath), "BKP-6: 已提交的 staging 应被清理");
        // sentinel 应被删除
        Assert(!recoveryDb.HasImportSentinel(testId), "BKP-6: 哨兵应在恢复后被删除");

        // 7. 崩溃恢复：sentinel 不存在 → 回滚
        Guid testId2 = Guid.NewGuid();
        string stagingPath2 = Path.Combine(recoveryPaths.Root, ".backup-import-staging", testId2.ToString("D"));
        Directory.CreateDirectory(stagingPath2);
        // 创建虚拟"已移动的 payload"
        string fakeFile = Path.Combine(recoveryPaths.Root, "images", "test-recovery2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeFile)!);
        File.WriteAllText(fakeFile, "test");
        var journal2 = new BackupImportJournal
        {
            ImportId = testId2.ToString("D"),
            StagingRoot = stagingPath2,
            FinalPaths = new List<string> { fakeFile },
            Phase = "pre_commit",
        };
        File.WriteAllText(Path.Combine(stagingPath2, "import-journal.json"),
            System.Text.Json.JsonSerializer.Serialize(journal2));
        // 不写哨兵

        recovery.RecoverAll();
        // sentinel 不存在 → 应删除 payload 和 staging
        Assert(!File.Exists(fakeFile), "BKP-7: 未提交的 payload 应被删除");
        Assert(!Directory.Exists(stagingPath2), "BKP-7: 未提交的 staging 应被清理");

        // 8. 真实恶意 ZIP / journal / SQLite 对抗套件（全部仅使用本次 selftest 临时目录）
        BackupSecuritySelfTest.Run(dir, archivePath);

        // 清理
        try { File.Delete(archivePath); } catch { }
    }

    // ══════════════════════════════════════════════
    //  M6.1b 新增：主题服务烟雾测试
    // ══════════════════════════════════════════════

    private static void RunThemeServiceTests()
    {
        // ThemeService 零依赖构造
        var theme = new ThemeService();

        // ColorMode：Light / Dark / System / 无效值
        string[] colorModes = { "Light", "Dark", "System", "FooBar" };
        foreach (string mode in colorModes)
        {
            try { theme.ApplyColorMode(mode); }
            catch (Exception ex)
            {
                Assert(false, $"ApplyColorMode('{mode}') 不应抛异常: {ex.Message}");
            }
        }

        // VisualTheme: Fluent / LiquidGlass / invalid value.
        string[] themes = { "Fluent", "LiquidGlass", "UnknownTheme" };
        foreach (string name in themes)
        {
            try { theme.ApplyVisualTheme(name); }
            catch (Exception ex)
            {
                Assert(false, $"ApplyVisualTheme('{name}') 不应抛异常: {ex.Message}");
            }
        }

        // —— M8 液态玻璃覆盖字典契约 ——
        // 动态覆盖字典带 marker；Fluent 时必须缺席，LiquidGlass 时必须位于末尾。
        static bool GlassPresent()
        {
            if (Application.Current is not { } app) return false;
            foreach (var d in app.Resources.MergedDictionaries)
                if (d.Contains(ThemeService.DynamicGlassMarkerKey)) return true;
            return false;
        }
        static bool GlassIsLast()
        {
            if (Application.Current is not { } app) return false;
            var dicts = app.Resources.MergedDictionaries;
            return dicts.Count > 0
                && dicts[dicts.Count - 1].Contains(ThemeService.DynamicGlassMarkerKey);
        }
        static byte AlphaOf(string key)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
                return brush.Color.A;
            Assert(false, $"{key} should resolve to a SolidColorBrush");
            return 0;
        }

        // 切玻璃：覆盖字典必须在末尾（优先级最高）、窗口背景 = Acrylic、画刷键可解析。
        Assert(Math.Abs(ThemeService.EffectiveLiquidGlassTransparency(55) - 60.25d) < 0.001,
            "LiquidGlass user value 55 should map to effective transparency 60.25%");
        theme.ApplyColorMode("Light");
        theme.ApplyLiquidGlassTransparency(55);
        theme.ApplyVisualTheme("LiquidGlass");
        Assert(GlassIsLast(), "切 LiquidGlass 后玻璃覆盖字典应为最后一个合并字典");
        Assert(theme.CurrentBackdrop == Wpf.Ui.Controls.WindowBackdropType.Acrylic,
            "LiquidGlass 的 CurrentBackdrop 应为 Acrylic");
        // 线性映射 Max=0x66: value=55 → t=0.55, bgAlpha≈0x2E
        Assert(AlphaOf("CardBackgroundFillColorDefaultBrush") == 0x99,
            "LiquidGlass light default (55) CardBackground alpha should be #99 (t=0.55)");
        Assert(AlphaOf("ApplicationBackgroundBrush") == 0x2E,
            "LiquidGlass light default (55) background overlay alpha should be ~18% opaque");
        Assert(AlphaOf(ThemeService.WindowOverlayKey) == 0x2E,
            "LiquidGlass light default (55) window overlay alpha should match background");
        theme.ApplyLiquidGlassTransparency(0);
        // value=0 → t=0, bgAlpha=0x66
        Assert(AlphaOf("CardBackgroundFillColorDefaultBrush") == 0xCC,
            "LiquidGlass light min (0) CardBackground alpha should be baseline #CC (t=0)");
        Assert(AlphaOf("ApplicationBackgroundBrush") == ThemeService.MaxBackgroundOverlayAlpha,
            "LiquidGlass light min slider should make background overlay most opaque");
        Assert(AlphaOf(ThemeService.WindowOverlayKey) == ThemeService.MaxBackgroundOverlayAlpha,
            "LiquidGlass light min slider should make window overlay most opaque");
        theme.ApplyLiquidGlassTransparency(100);
        // value=100 → t=1.0, bgAlpha=0
        Assert(AlphaOf("CardBackgroundFillColorDefaultBrush") == 0x6F,
            "LiquidGlass light max (100) CardBackground alpha should be most transparent #6F (t=1)");
        Assert(AlphaOf("ApplicationBackgroundBrush") == 0x00,
            "LiquidGlass light max slider should make background overlay fully transparent");
        Assert(AlphaOf(ThemeService.WindowOverlayKey) == 0x00,
            "LiquidGlass light max slider should make window overlay fully transparent");

        theme.ApplyColorMode("Dark");
        theme.ApplyLiquidGlassTransparency(55);
        Assert(GlassIsLast(), "切 Dark 后玻璃覆盖字典仍应为最后一个合并字典");
        Assert(AlphaOf("CardBackgroundFillColorDefaultBrush") == 0x5C,
            "LiquidGlass dark default (55) CardBackground alpha should be #5C (t=0.55)");
        Assert(AlphaOf("ApplicationBackgroundBrush") == 0x2E,
            "LiquidGlass dark default (55) background overlay alpha ~18% opaque");
        Assert(AlphaOf(ThemeService.WindowOverlayKey) == 0x2E,
            "LiquidGlass dark default (55) window overlay alpha should match background");
        theme.ApplyLiquidGlassTransparency(0);
        // value=0 → t=0, bgAlpha=0x66
        Assert(AlphaOf("CardBackgroundFillColorDefaultBrush") == 0x85,
            "LiquidGlass dark min (0) CardBackground alpha should be baseline #85 (t=0)");
        Assert(AlphaOf("ApplicationBackgroundBrush") == ThemeService.MaxBackgroundOverlayAlpha,
            "LiquidGlass dark min slider should make background overlay most opaque");
        Assert(AlphaOf(ThemeService.WindowOverlayKey) == ThemeService.MaxBackgroundOverlayAlpha,
            "LiquidGlass dark min slider should make window overlay most opaque");
        theme.ApplyLiquidGlassTransparency(100);
        // value=100 → t=1.0, bgAlpha=0
        Assert(AlphaOf("CardBackgroundFillColorDefaultBrush") == 0x3B,
            "LiquidGlass dark max (100) CardBackground alpha should be most transparent #3B (t=1)");
        Assert(AlphaOf("ApplicationBackgroundBrush") == 0x00,
            "LiquidGlass dark max slider should make background overlay fully transparent");
        Assert(AlphaOf(ThemeService.WindowOverlayKey) == 0x00,
            "LiquidGlass dark max slider should make window overlay fully transparent");

        var window = new Wpf.Ui.Controls.FluentWindow();
        theme.AttachWindow(window);
        Assert(window.WindowBackdropType == Wpf.Ui.Controls.WindowBackdropType.Acrylic,
            "热切换：已附加窗口应立即同步 LiquidGlass 的 Acrylic 背景");

        // 切回 Fluent：覆盖字典必须缺席（保证 Fluent 视觉基线不变）、背景回 Mica。
        theme.ApplyVisualTheme("Fluent");
        Assert(!GlassPresent(), "切回 Fluent 后玻璃覆盖字典应从合并字典中缺席");
        Assert(theme.CurrentBackdrop == Wpf.Ui.Controls.WindowBackdropType.Mica,
            "Fluent 的 CurrentBackdrop 应为 Mica");
        Assert(window.WindowBackdropType == Wpf.Ui.Controls.WindowBackdropType.Mica,
            "热切换：切回 Fluent 后已附加窗口应同步 Mica 背景");
        Assert(Application.Current?.TryFindResource("CardBackgroundFillColorDefaultBrush") is not null,
            "Fluent 下 CardBackgroundFillColorDefaultBrush 仍应可解析");

        // 末态收敛为 Fluent，不污染后续测试 / 进程退出前保持干净。
    }

    // ══════════════════════════════════════════════
    //  M6.1b 新增：备份导入恢复 RecoverAll 测试
    // ══════════════════════════════════════════════

    private static void RunBackupRecoveryTests(string root, Database db)
    {
        var recovery = new BackupImportRecoveryService(db, root);

        // 场景 1：无 staging 目录 → RecoverAll 幂等返回
        string stagingRoot = Path.Combine(root, ".backup-import-staging");
        if (Directory.Exists(stagingRoot))
        {
            try { Directory.Delete(stagingRoot, true); } catch { }
        }

        try { recovery.RecoverAll(); }
        catch (Exception ex) { Assert(false, $"无 staging 时 RecoverAll 不应抛异常: {ex.Message}"); }

        // 场景 2：有 journal 无 sentinel → 应清理 staging
        string importId = Guid.NewGuid().ToString("D");
        string stagingDir = Path.Combine(stagingRoot, importId);
        Directory.CreateDirectory(stagingDir);

        string fakePayload = Path.Combine(stagingDir, "some-payload.txt");
        File.WriteAllText(fakePayload, "test");

        var journal = new BackupImportJournal
        {
            ImportId = importId,
            StagingRoot = stagingDir,
            FinalPaths = new List<string> { fakePayload },
            Phase = "pre_commit",
        };
        File.WriteAllText(Path.Combine(stagingDir, "import-journal.json"),
            System.Text.Json.JsonSerializer.Serialize(journal));

        // 不写 sentinel → RecoverAll 应清理
        try { recovery.RecoverAll(); }
        catch (Exception ex) { Assert(false, $"有 journal 无 sentinel 时 RecoverAll 不应抛异常: {ex.Message}"); }

        // 验证清理
        Assert(!File.Exists(fakePayload), "M6.1b-BR1: 无 sentinel 的 payload 应被清理");
        Assert(!Directory.Exists(stagingDir), "M6.1b-BR2: 无 sentinel 的 staging 目录应被删除");

        // 清理残留
        try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); } catch { }
    }
}
