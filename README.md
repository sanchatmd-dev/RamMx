# RamMx

แอป Windows สำหรับดู RAM และ process เข้าใจว่าอะไรเป็นส่วนของระบบ เลือกปิดแอป ถอนติดตั้ง และล้าง cache ที่ระบุขอบเขตได้ พร้อมจำการตั้งค่าไว้ในเครื่อง

**v0.1.0 Preview — แอป native ที่ใช้งานได้จริง มีการทดสอบอัตโนมัติและหน้าจอ แต่ยังไม่ใช่ release ที่ผ่านทุก production gate** ดู [ผลการทดสอบ](docs/TESTING.md)

## ความสามารถ

| ส่วน | สิ่งที่ทำได้ |
|---|---|
| ภาพรวม | Available/usable/installed RAM, commit/limit, disk free และกราฟ RAM |
| แอปที่ทำงาน | process grouping, private working set, CPU, ประเภทระบบ/แอป/runtime และ path |
| ควบคุมแอป | ขอปิดตามปกติและ force stop แยกคำสั่ง ตรวจ identity/critical status ซ้ำ |
| คำแนะนำ | โปรไฟล์, Keep list, รายการที่ผู้ใช้ระบุว่าไม่ใช้ และ pressure ที่ต่อเนื่อง |
| ตรวจสอบเบื้องหลัง | รายการควรตรวจจาก path/แนวโน้ม RAM, Authenticode แบบ offline และ startup associations พร้อมเหตุผล |
| ถอนติดตั้ง | registry/MSIX inventory; native MSI/EXE/MSIX uninstall และตรวจ registration หลังทำ |
| Cache / leftovers | preview และเลือกลบ cache ของ Chrome, Edge, VS Code, Discord และ Slack |
| การตั้งค่า | SQLite, dark/light, refresh interval, profiles, thresholds, tray, startup, import/export |
| ประวัติ | intent/result, partial failure และ unknown outcome หลังปิดผิดปกติ |

ไม่มีการปิดแอปอัตโนมัติ ล้าง standby memory ปรับ pagefile หรือ registry cleaner

## เปิดใช้งาน

ต้องใช้ **Windows 11 x64** แพ็กเกจ self-contained ไม่ต้องติดตั้ง .NET runtime เพิ่ม

1. ดาวน์โหลด `RamMx-win-x64` artifact จาก [GitHub Actions](https://github.com/sanchatmd-dev/RamMx/actions) ของ commit ที่ build ผ่าน หรือ build จาก source ตามด้านล่าง
2. แตกไฟล์ทั้งหมดไปยังโฟลเดอร์เดียวกัน
3. เปิด `RamMx.exe` ด้วยสิทธิ์ผู้ใช้ปกติ
4. เลือกแอปแล้วอ่านรายละเอียดก่อนปิดหรือถอน

ถ้า RamMx อยู่ใน tray ให้เปิด `RamMx.exe` อีกครั้งเพื่อเรียกหน้าต่างเดิมกลับมา แพ็กเกจมี `build-info.json` ระบุ source commit และมีไฟล์ SHA-256 คู่กับ ZIP สำหรับตรวจว่าไฟล์ตรงกัน

Preview ยังไม่มีลายเซ็นผู้เผยแพร่ Authenticode ไม่มีการสร้างใบรับรองที่เครื่องคุณต้องเชื่อถือเอง คำสั่ง Admin เป็นตัวเลือกแยกและ Windows จะแสดง UAC ตามปกติ

### ปิดแอป

- **ปิดตามปกติ** ส่งคำขอไปยังหน้าต่าง แอปอาจขอให้บันทึกงาน ไม่มี force ตาม timeout
- **Force stop** ยุติเฉพาะ process instances ที่ตรวจแล้ว งานยังไม่บันทึกอาจสูญหาย
- **Force stop · Admin** ใช้ helper ที่ยกระดับและยืนยันเป้าหมายอีกครั้ง ไม่ข้าม critical/protected policy
- กลุ่ม runtime เช่น Node/Python/Java และ shared hosts ไม่ถูกเสนอให้หยุดรวมทั้งก้อน
- Keep list ป้องกันคำแนะนำปิด และถามเพิ่มเมื่อสั่งปิดด้วยมือ

### ถอนติดตั้งและล้างข้อมูล

ใช้กลไกผู้ผลิต/Windows ก่อน แล้วเลือกตรวจ cache ที่รองรับต่อได้ **Full uninstall หมายถึงถอนและเก็บตกภายในขอบเขตที่ตรวจสอบได้ ไม่ใช่รับรองว่าไฟล์/registry ของทุกแอปหาย 100%**

ตัวถอนบางตัวรวมถึง MSIX อาจลบ app data/settings ควรสำรองผ่านแอปก่อนถอน RamMx ไม่แสดงตัวเลือกเก็บข้อมูลที่ทำตามไม่ได้ Cache cleanup ไม่รวม cookies, login profiles, bookmarks, documents, settings และ save games

ต้องปิดแอปก่อน cleanup ไฟล์ที่เลือกจะถูกลบ **ถาวร** หลัง preview ไม่ผ่าน Recycle Bin ไฟล์ที่เปลี่ยนหลังสแกน ถูกล็อก เป็น reparse point หรือมี hard links จะถูกข้าม ขนาดไฟล์ที่ลบแยกจากพื้นที่ว่างที่สังเกตได้จริง

### ตัวเลขและหลักฐาน

- RAM เฉพาะแอปคือ private working set เมื่อ API รองรับ `*` หมายถึงข้อมูลบาง process อ่านไม่ได้
- หาก Windows ไม่ให้เปิดอ่าน process โดยตรง จะใช้ Windows Performance Counters เป็นทางสำรอง อ่าน private working set ได้ด้วยสิทธิ์ปกติ และแสดงแหล่งค่าในรายละเอียด หากทั้งสองทางอ่านไม่ได้จะคงสถานะ “อ่านไม่ได้” ไม่แทนด้วยศูนย์ ค่าต่ำกว่า 1 MiB แสดงเป็น KiB/B
- Commit ไม่ใช่ physical RAM ที่ resident ทั้งหมด และไม่ควรบวก working set ทุก process เป็นยอดทั้งเครื่อง
- Unsigned, background activity หรือ RAM สูงอย่างเดียวไม่ใช่ malware verdict
- Defender ใช้ provider/policy ที่มีอยู่ ไม่เปลี่ยน exclusions หรือปิดการป้องกัน

## Build และทดสอบ

ติดตั้ง [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) บน Windows แล้วรัน PowerShell:

```powershell
git clone https://github.com/sanchatmd-dev/RamMx.git
cd RamMx
./scripts/build.ps1 -Publish
./artifacts/RamMx/RamMx.exe
```

สคริปต์ restore, build, test และสร้าง `artifacts/RamMx-win-x64.zip` ถ้ามี SDK ใน `.tools/dotnet` จะใช้ตัวนั้นแทน

```powershell
dotnet build RamMx.sln -c Release
dotnet test tests/RamMx.Tests/RamMx.Tests.csproj -c Release --no-build
dotnet publish src/RamMx.App/RamMx.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/RamMx
```

Solution build สร้าง MemoryFixture ก่อน integration tests ซึ่งใช้ process และ HKCU registration `RamMx.IntegrationFixture` ที่สร้างเฉพาะทดสอบ ไม่ถอนแอปจริงของผู้ใช้

## ข้อมูลส่วนตัว

ฐานข้อมูลอยู่ที่ `%LOCALAPPDATA%\RamMx\rammx.db` ไม่ส่ง process list, paths หรือประวัติไป cloud และไม่มี AI/network dependency ในเส้นทางปิดหรือลบ

Settings export อาจมี paths และความชอบส่วนตัว ควรตรวจก่อนแชร์ SQLite ไม่ได้เข้ารหัสทั้งฐาน ห้าม commit database/logs/certificates หรือ snapshot เครื่องจริง

## ขอบเขต Preview

- App mapping อาจไม่ครอบคลุม portable apps และ shared runtime ownership
- Startup แสดง Run entries/Startup folders; เปลี่ยน startup แอปอื่นผ่าน Windows Settings
- Cache recipes จำกัดห้าแอปและ layout ที่ระบุ ไม่ลบโฟลเดอร์ชื่อคล้ายกันทั่วดิสก์
- ไม่มี generic service/driver control, antivirus replacement หรือ auto-update
- Native uninstall ไม่ใช่ transaction ที่ย้อนคืน personal data ได้
- Elevated broker, native MSI/MSIX mutation และ Defender remediation ต้องทดสอบเพิ่มเติมใน VM และกับ UAC จริงก่อนผ่าน production gates
- ยังไม่มี 24-hour soak certification หรือ signed installer ผลวัดระยะสั้นไม่แทนการตรวจเหล่านี้

## เอกสาร

- [Context.md](Context.md) — สถาปัตยกรรมและกฎสำหรับพัฒนาต่อ
- [DESIGN.md](docs/DESIGN.md) — การไหลของข้อมูลและ action
- [TESTING.md](docs/TESTING.md) — การทดสอบและข้อจำกัด

ไฟล์ research ที่มี snapshot เครื่องจริงไม่ถูกส่งขึ้น repo สาธารณะ
