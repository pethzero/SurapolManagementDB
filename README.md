# SurapolManagementDB — Demo

โปรแกรมจัดการฐานข้อมูล **MySQL / MariaDB** บน Windows แบบใช้ง่าย (คล้าย Navicat / DBeaver ฉบับย่อ)
repo นี้เป็น **รุ่น Demo** — เขียนด้วย C# WinForms (.NET 11)

*A simple MySQL / MariaDB client for Windows. This repository contains the **demo edition**.*

## รุ่น Demo ทำอะไรได้

- **เชื่อมต่อ** MySQL / MariaDB (จำการเชื่อมต่อไว้ เลือกจำรหัสผ่านได้ — เก็บแบบเข้ารหัสด้วย Windows DPAPI)
- **SQL Editor** — พิมพ์และรันคำสั่ง SQL (Ctrl+Enter / F9), ดูผลลัพธ์เป็นตาราง, สร้างคำสั่ง SELECT / INSERT / UPDATE / DELETE จากตาราง
- **ดูข้อมูลในตาราง** (อ่านอย่างเดียว) — แบ่งหน้า, กรองด้วย WHERE, เรียงตามคอลัมน์, ดูโครงสร้างและ DDL, เปิดดู / บันทึก BLOB (รูปภาพ / ไฟล์)
- ค้นหาฐานข้อมูล / ตาราง, ธีมมืด / สว่าง, ภาษาไทย / English

| | Demo | Full |
|---|:---:|:---:|
| เชื่อมต่อ MySQL / MariaDB | ✅ | ✅ |
| SQL Editor | ✅ | ✅ |
| ดูข้อมูลในตาราง / โครงสร้าง / DDL | ✅ | ✅ |
| เพิ่ม / แก้ / ลบข้อมูลในตาราง | — | ✅ |
| สร้าง / ลบฐานข้อมูล, จัดการผู้ใช้, Session Manager | — | ✅ |
| Backup / Restore | — | ✅ |
| เปิดไฟล์ Navicat Backup (.nb3), แปลงเป็น MySQL / MariaDB / PostgreSQL / CSV | — | ✅ |

## ดาวน์โหลด

ดูที่หน้า [**Releases**](../../releases) → โหลด `SurapolManagementDB-Demo-…-win-x64.zip` แตกไฟล์แล้วดับเบิลคลิก `SurapolManagementDB-Demo.exe`
ไม่ต้องติดตั้ง .NET (Windows 10 / 11 แบบ 64-bit)

ครั้งแรกที่เปิด ถ้า Windows ขึ้น "Windows protected your PC" ให้กด **More info → Run anyway**

## Build จากโค้ด

ต้องมี [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)

```
git clone https://github.com/pethzero/SurapolManagementDB.git
cd SurapolManagementDB
dotnet run --project SurapolManagementDB
```

หรือดับเบิลคลิก

- `build.bat` → `publish\SurapolManagementDB-Demo.exe` (เครื่องที่ใช้ต้องมี .NET 11 Desktop Runtime)
- `build-portable.bat` → `publish-portable\SurapolManagementDB-Demo.exe` (ไฟล์เดียว ไม่ต้องติดตั้ง .NET)

## โครงสร้างโค้ด

```
SurapolManagementDB\
  Program.cs          จุดเริ่มโปรแกรม
  DbForm.cs           หน้าต่างหลัก: การเชื่อมต่อ, ต้นไม้ฐานข้อมูล / ตาราง
  DbForm.Sql.cs       SQL Editor
  DbForm.Table.cs     ดูข้อมูลในตาราง / BLOB
  Connections.cs      รายการการเชื่อมต่อ + หน้าต่างเชื่อมต่อ
  Theme.cs, Ui.cs     ธีม ฟอนต์ ไอคอน
  Core\               เชื่อมต่อฐานข้อมูล / อ่านค่า, ข้อความ 2 ภาษา
```

ค่าตั้งและรายการการเชื่อมต่อเก็บที่ `%LOCALAPPDATA%\SurapolManagementDB Demo\`

---

เวอร์ชัน 1.0.0 · Demo edition
