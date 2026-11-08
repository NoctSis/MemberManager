# -*- coding: utf-8 -*-
r"""Rebuild the two small release zips from the CURRENT binaries.

    release\收银系统-安装版.zip    (light installer, flat: exe + 说明.txt)
    release\收银系统-免安装版.zip  (green, flat: files at zip root, no data/)

Source of truth is beta\ + web\dist, never an old zip. Traps handled here:

  * beta\data, beta\logs, beta\backups, beta\exports must NEVER enter the green
    zip - they hold the shop's real database and the demo exports.
  * beta\web only refreshes when compile-inprocess.ps1 runs; web\dist is the
    freshest vite output, so the green zip takes web\dist.
  * empty-seed.txt is written by us at the end (the copy in beta\ has the same
    content); skip it during the walk so zipfile does not store it twice.
  * old zips had an extra _tmp_pkg\ / _g\ level - everything here is flat.
"""
import os
import sys
import zipfile

# Repo root derived from this file's location (tools\ -> repo). Keeps the script
# portable: a fresh clone on any machine packs without editing a hardcoded path.
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BETA = os.path.join(ROOT, "beta")
WEB_SRC = os.path.join(ROOT, "web", "dist")
REL = os.path.join(ROOT, "release")

GREEN_OUT = os.path.join(REL, "收银系统-免安装版.zip")
INSTALL_OUT = os.path.join(REL, "收银系统-安装版.zip")
WIN7_OUT = os.path.join(REL, "收银系统-Win7离线版.zip")
LIGHT_EXE = os.path.join(REL, "收银系统-安装程序-轻量版.exe")
FULL_EXE = os.path.join(REL, "会员管家-安装程序-1.0.1.exe")

APP_FILES = [
    "MemberManager.exe", "MemberManager.exe.config",
    "MemberManager.Models.dll", "MemberManager.Common.dll",
    "MemberManager.DAL.dll", "MemberManager.BLL.dll",
    "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
    "Newtonsoft.Json.dll", "System.Data.SQLite.dll", "SQLite.Interop.dll",
    "WebView2Loader.dll", "portable.txt",
]
SEED = "empty seed mode: only system-required rows are seeded\n"

GREEN_README = """收银系统 · 免安装版
============================

解压到任意文件夹（U 盘也行），双击 MemberManager.exe 直接启动。
不用安装，不需要管理员权限，数据就存在这个文件夹的 data\\ 里，整个文件夹可以拷走。

前提（Windows 10 / 11 一般都已经具备）：
  · .NET Framework 4.8
  · 微软 Edge 的 WebView2 内核
缺这两样的 Windows 7 电脑，请改用「Win7 离线版」安装包。

首次启动会自动建一个空数据库，没有任何演示数据。

登录账号：店长
初始密码：123456（登录后请立刻改密码）

换电脑：整个文件夹拷过去即可，data\\ 里就是全部会员和流水。

卸载：本软件不写注册表、不在系统里留服务或自启动。
      直接删掉整个文件夹就是卸载；删之前请先把 data\\ 拷走备份。
      如果你把单独导出的 Excel 报表也放在这个文件夹里，记得先拷走——
      它们不是运行数据，别跟着文件夹一起删了。
      如果桌面上有手动建的快捷方式，一起删掉即可。
"""

WIN7_README = """收银系统 · Win7 离线安装版
============================

双击「收银系统-安装程序.exe」，一路下一步即可。
不需要联网，也不需要事先装任何运行环境：
  · .NET Framework 4.8（缺了会自动静默安装）
  · WebView2 内核 109（Win7 最高只能用到这一版，随包自带）

装在当前用户目录（%LOCALAPPDATA%\\MemberManager），不需要管理员权限。
装完首次启动会自动建一个空数据库，没有任何演示数据。

登录账号：店长
初始密码：123456（登录后请立刻改密码）

升级：直接运行新版安装包覆盖安装即可，会员数据和流水都会保留。

卸载（安装版一定带卸载，两个入口）：
  1. 开始菜单 →「会员管家」→「卸载 会员管家」
  2. 控制面板 → 程序和功能 →「会员管家 1.0.1」→ 卸载
  卸载会删掉程序文件、随包的浏览器内核、快捷方式、开机自启，
  并问要不要一并删除本程序目录里的运行数据（会员库、备份、操作日志），默认「保留」。
  你单独导出的 Excel 报表、以及程序目录以外的任何文件，一律不动。
"""

INSTALL_README = """收银系统 · 安装版（轻量）
============================

双击「收银系统-安装程序.exe」，一路下一步即可。

这个包不带运行环境（约 3.6MB），适合 Windows 10 / 11：
  · 系统自带 .NET Framework 4.8
  · 系统自带 Edge 的 WebView2 内核
如果提示缺少 .NET Framework 4.8，请改用「Win7 离线版」。

装在当前用户目录（%LOCALAPPDATA%\\MemberManager），不需要管理员权限。
装完首次启动会自动建一个空数据库，没有任何演示数据。

登录账号：店长
初始密码：123456（登录后请立刻改密码）

升级：直接运行新版安装包覆盖安装即可，会员数据和流水都会保留。

卸载（安装版一定带卸载，两个入口）：
  1. 开始菜单 →「会员管家」→「卸载 会员管家」
  2. 控制面板 → 程序和功能 →「会员管家 1.0.1」→ 卸载
  卸载会删掉程序文件、桌面和开始菜单快捷方式、开机自启，
  并问要不要一并删除本程序目录里的运行数据（会员库、备份、操作日志），默认「保留」。
  你单独导出的 Excel 报表、以及程序目录以外的任何文件，一律不动。
"""


def die(msg):
    print("ERROR: " + msg)
    return 2


def build_green():
    for label, path, probe in (("web", WEB_SRC, "index.html"), ("beta", BETA, "MemberManager.exe")):
        if not os.path.isfile(os.path.join(path, probe)):
            return die("%s SOURCE MISSING: %s" % (label, path))
    missing = [n for n in APP_FILES if not os.path.isfile(os.path.join(BETA, n))]
    if missing:
        return die("missing from beta: " + ", ".join(missing))
    if not os.path.isfile(os.path.join(BETA, "empty-seed.txt")):
        print("WARN: beta\\empty-seed.txt missing, writing the canonical one")

    # ZipFile(..., "w") truncates in place - no explicit unlink (owner rule:
    # never permanently delete files; overwriting our own release zip is fine).
    n_web = 0
    with zipfile.ZipFile(GREEN_OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for name in APP_FILES:
            z.write(os.path.join(BETA, name), name)
        for dirpath, dirnames, filenames in os.walk(WEB_SRC):
            rel = os.path.relpath(dirpath, WEB_SRC)
            for f in filenames:
                full = os.path.join(dirpath, f)
                arc = "web/" + f if rel == "." else "web/" + os.path.join(rel, f).replace("\\", "/")
                z.write(full, arc)
                n_web += 1
        z.writestr("empty-seed.txt", SEED)
        z.writestr("使用说明.txt", GREEN_README)

    size = os.path.getsize(GREEN_OUT)
    print("green   : app=%d web=%d  %.2f MB  -> %s" % (len(APP_FILES), n_web, size / 1048576.0, GREEN_OUT))
    return 0


def build_installer():
    if not os.path.isfile(LIGHT_EXE):
        return die("light installer missing, run: ISCC /DLIGHT=1 setup.iss")
    with zipfile.ZipFile(INSTALL_OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        z.write(LIGHT_EXE, "收银系统-安装程序.exe")
        z.writestr("使用说明.txt", INSTALL_README)
    size = os.path.getsize(INSTALL_OUT)
    print("install : %.2f MB  -> %s" % (size / 1048576.0, INSTALL_OUT))
    return 0


def build_win7():
    if not os.path.isfile(FULL_EXE):
        return die("full installer missing, run: ISCC setup.iss")
    # The 284MB installer is already LZMA-compressed; re-zipping it with
    # ZIP_STORED avoids burning a minute trying to compress incompressible data.
    with zipfile.ZipFile(WIN7_OUT, "w", zipfile.ZIP_STORED) as z:
        z.write(FULL_EXE, "收银系统-安装程序.exe")
    with zipfile.ZipFile(WIN7_OUT, "a", zipfile.ZIP_DEFLATED) as z:
        z.writestr("安装说明.txt", WIN7_README)
    size = os.path.getsize(WIN7_OUT)
    print("win7    : %.2f MB  -> %s" % (size / 1048576.0, WIN7_OUT))
    return 0


if __name__ == "__main__":
    rc = build_green()
    if rc == 0:
        rc = build_installer()
    if rc == 0:
        rc = build_win7()
    sys.exit(rc)
