# SurapolManagementDB — Demo

โปรแกรมจัดการฐานข้อมูล **MySQL / MariaDB** บน Windows แบบใช้ง่าย (คล้าย Navicat / DBeaver ฉบับย่อ)
repo นี้เป็น **รุ่น Demo** — เขียนด้วย C# WinForms (.NET 11)

*A simple MySQL / MariaDB client for Windows. This repository contains the **demo edition**.*

## รุ่น Demo ทำอะไรได้

- **เชื่อมต่อ** MySQL / MariaDB (จำการเชื่อมต่อไว้ เลือกจำรหัสผ่านได้ — เก็บแบบเข้ารหัสด้วย Windows DPAPI)
- **SQL Editor** — พิมพ์และรันคำสั่ง SQL (Ctrl+Enter / F9), ดูผลลัพธ์เป็นตาราง, สร้างคำสั่ง SELECT / INSERT / UPDATE / DELETE จากตาราง
- **ดูข้อมูลในตาราง** (อ่านอย่างเดียว) — หน้าจอแบบ หัว / ตัว / ท้าย (คล้าย DBeaver): กรองด้วย WHERE, กรองตามค่าในช่อง, แบ่งหน้า (50–5,000 แถว), เรียง / ลาก / ซ่อนคอลัมน์, หัวคอลัมน์บอกชนิดข้อมูล
- โหมดดู **Grid / Text / Record**, หน้าต่างดูทีละแถว (ดับเบิลคลิกหัวแถว), เลือกเป็นกลุ่มแล้วคัดลอก (Tab / พร้อมหัวคอลัมน์ / เป็น INSERT)
- ดูโครงสร้างและ DDL, เปิดดู / บันทึก BLOB (รูปภาพ / ไฟล์)
- ค้นหาฐานข้อมูล / ตาราง, ธีมมืด / สว่าง, ภาษาไทย / English

| | Demo | Full |
|---|:---:|:---:|
| เชื่อมต่อ MySQL / MariaDB | ✅ | ✅ |
| SQL Editor | ✅ | ✅ |
| ดูข้อมูลในตาราง / โครงสร้าง / DDL | ✅ | ✅ |
| เพิ่ม / แก้ / ลบข้อมูลในตาราง, วางทั้งกลุ่ม | — | ✅ |
| แก้โครงสร้างตาราง (ALTER TABLE) | — | ✅ |
| สร้าง / ลบฐานข้อมูล, จัดการผู้ใช้, Session Manager | — | ✅ |
| Backup / Restore (เปลี่ยน charset / collation / รุ่น server) | — | ✅ |
| Export ตารางเป็น CSV / TXT, Log กิจกรรม (Maintenance) | — | ✅ |
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
  DbForm.Table.cs     ดูข้อมูลในตาราง / BLOB / คัดลอก
  RowEditForm.cs      หน้าต่างดูทีละแถว
  TextGrid.cs         โหมดดูแบบข้อความ
  Connections.cs      รายการการเชื่อมต่อ + หน้าต่างเชื่อมต่อ
  Theme.cs, Ui.cs     ธีม ฟอนต์ ไอคอน
  Core\               เชื่อมต่อฐานข้อมูล / อ่านค่า, ข้อความ 2 ภาษา
```

ค่าตั้งและรายการการเชื่อมต่อเก็บที่ `%LOCALAPPDATA%\SurapolManagementDB Demo\`

## ประวัติการพัฒนา (Changelog)

### 1.1.0 — 2026-11-21
- หน้าข้อมูลตารางใหม่แบบ หัว / ตัว / ท้าย: ช่องกรอง WHERE ยาวด้านบน, ปุ่มไอคอนเล็กด้านล่าง
- หัวคอลัมน์แสดงชนิดข้อมูล (`[PK]`) ทั้งหน้าข้อมูลและผล SQL
- ลากหัวคอลัมน์สลับลำดับ, ซ่อน / เลือกคอลัมน์, กรองตามค่าในช่อง, เลือกจำนวนแถวต่อหน้า
- โหมดดู Grid / Text / Record (Text จัดคอลัมน์ภาษาไทยตรง)
- หน้าต่างดูทีละแถว: ดับเบิลคลิกหัวแถว ชี้ช่องที่เลือก, ◀ ▶ เลื่อนแถว
- เลือกเป็นกลุ่มแล้วคัดลอก (Ctrl+C / Ctrl+Shift+C พร้อมหัวคอลัมน์ / เป็น INSERT)
- ค้นหาตาราง / ฐานข้อมูล
- (ตัวเต็มเพิ่ม: แก้โครงสร้างตาราง, วางทั้งกลุ่ม, Export CSV / TXT, Restore เปลี่ยน charset / collation / รุ่น server, Log กิจกรรม)

### 1.0.0 — 2026-11-18
- รุ่น Demo แรก: เชื่อมต่อ MySQL / MariaDB, SQL Editor, ดูข้อมูลในตาราง / โครงสร้าง / DDL / BLOB, ธีมมืด / สว่าง, ภาษาไทย / English

---

เวอร์ชัน 1.1.0 · Demo edition
