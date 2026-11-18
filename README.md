ระบบขออนุมัติจ่ายค่าเช่าออนไลน์และเชื่อมต่อระบบบัญชีเจ้าหนี้ (ERP Integration) พัฒนาด้วย **C# / ASP.NET Core (.NET 10)** และ **Microsoft SQL Server**

## ความต้องการของระบบ (Prerequisites)

- **.NET SDK:** .NET 10.0 SDK ขึ้นไป
- **Database:** Microsoft SQL Server (2019 / 2022 / Express / Developer Edition)
- **IDE / Tools:** Visual Studio 2022/2026 


## วิธีการติดตั้งและรันระบบ (Installation & Running Guide)

ขั้นตอนที่ 1: ติดตั้งฐานข้อมูล (SQL Server)
1. เปิดโปรแกรม SQL Server Management Studio (SSMS) และเชื่อมต่อเข้ากับ SQL Server ของเครื่อง (`localhost`)
2. เปิดไฟล์สคริปต์ BCG_RPAS.sql
3. กด Execute 
   - สคริปต์จะสร้างฐานข้อมูลชื่อ `BCG_RPAS` พร้อมตาราง, เงื่อนไขตรวจสอบข้อมูล (Check Constraints), และข้อมูลตั้งต้น (Master Data, ผู้ใช้งานทุกบทบาท, สายการอนุมัติ, และตัวอย่างใบคำขอค่าเช่า) โดยอัตโนมัติ

### ขั้นตอนที่ 2: ตรวจสอบการตั้งค่า Connection String
เปิดไฟล์ [`BCG_RPAS_API/appsettings.json`](./BCG_RPAS_API/appsettings.json) และตรวจสอบ `DefaultConnection` ให้ตรงกับ SQL Server ของเครื่อง:
"ConnectionStrings": {
  "DefaultConnection": "Data Source=localhost;Initial Catalog=BCG_RPAS;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;"
}
มีแนบไฟล์ไป appsettings.json

### ขั้นตอนที่ 3: รันโปรเจกต์ Backend API (`BCG_RPAS_API`)
ใช้ Visual Studio เปิดโฟลเดอร์โปรเจกต์ แล้วรัน

```
- **API Base URL:** `https://localhost:7050` (และ `http://localhost:5074`)
- **Swagger UI (สำหรับทดสอบ API):** `https://localhost:7050/swagger`
- **ไฟล์ Application Log รายวัน:** `BCG_RPAS_API/BCG_RPAS_API/Logs/api-YYYY-MM-DD.log`
```

## บัญชีผู้ใช้งานสำหรับทดสอบ (Test Accounts)
ข้อมูลตั้งต้นใน `BCG_RPAS.sql` เตรียมผู้ใช้งานครบทุกบทบาท โดยใช้รหัสผ่านเดียวกันทั้งหมดคือ **`1234`**:

| ชื่อผู้ใช้ (`UserName`) | รหัสผ่าน (`Password`) | บทบาท (`RoleCode`) | หน้าที่และสิทธิ์การใช้งาน |
| :--- | :--- | :--- | :--- |
| **`req01`** | `1234` | `REQUESTER` | พนักงานผู้ขออนุมัติหลัก (สร้าง, แก้ไข, แนบไฟล์ PDF, ส่งขออนุมัติ, ยกเลิกคำขอของตนเอง) |
| **`req02`** | `1234` | `REQUESTER` | พนักงานผู้ขออนุมัติคนที่ 2 (สำหรับทดสอบแยกสิทธิ์เจ้าของคำขอ) |
| **`mgr01`** | `1234` | `MANAGER` | หัวหน้าผู้อนุมัติขั้นที่ 1 (อนุมัติ, ส่งคืนแก้ไข, ปฏิเสธคำขอ, จัดการข้อมูลหลัก, ดูคิว ERP) |
| **`exec01`** | `1234` | `EXECUTIVE` | ผู้บริหารผู้อนุมัติขั้นที่ 2 สำหรับคำขอที่ยอดก่อน VAT > `50,000` บาท |
| **`admin`** | `1234` | `ADMINISTRATOR` | ผู้ดูแลระบบ (จัดการผู้ใช้, ผูกสายการอนุมัติ, จัดการข้อมูลหลัก, ตรวจสอบคำขอและคิว ERP ทั้งหมด) |

---

## สรุป REST API Endpoints หลัก

| หมวดหมู่ | HTTP Method & Endpoint | คำอธิบาย |
| :--- | :--- | :--- |
| **Auth** | `POST /api/auth/login` | เข้าสู่ระบบและรับ JWT Bearer Token | ************* เอาไปใส่ใน swagger Bearer header เพื่อใช้งาน API อื่นๆ **************** แนะนำ Account req01

| **Rental Requests** | `POST /api/rental-requests` | สร้างใบคำขอฉบับร่างใหม่ (`id = 0`) หรือแก้ไขใบคำขอเดิม (`id > 0`) |


***EX***
*CREATE* id = 0

curl -X 'POST' \
  'https://localhost:7050/api/rental-requests' \
  -H 'accept: */*' \
  -H 'Authorization: Bearer ***' \
  -H 'Content-Type: application/json' \
  -d '{
  "id": 0,
  "contractId": 1,
  "costCenterId": 1,
  "periodStartDate": "2026-11-18T08:48:16.255Z",
  "periodEndDate": "2026-11-18T08:48:16.255Z",
  "amount": 1000,
  "vatRate": 0,
  "notes": "string",
  "attachmentFileNames": [
    "string"
  ],
  "expectedConcurrencyVersion": 0
}'

จะได้ Response id มาเพื่อเอาไป GET ใส่ค่า {id} ในแต่ละ id มีตรวจสอบ สิทธิ์ผ่าน Berrer ว่าดูได้ไหม 
| | `GET /api/rental-requests/{id}` | ดูรายละเอียดใบคำขอ สถานะ สายการอนุมัติ และประวัติการดำเนินการทั้งหมด |
curl -X 'GET' \
  'https://localhost:7050/api/rental-requests/7' \
  -H 'accept: */*' \
  -H 'Authorization: Bearer ****'

| | `POST /api/rental-requests/{id}/submit` | ส่งใบคำขอเข้าสู่กระบวนการอนุมัติ (สร้าง Snapshot `RequestRevisions` และ `ApprovalSteps`) |
| | `POST /api/rental-requests/{id}/cancel` | ยกเลิกใบคำขอโดยผู้ขอ (เฉพาะก่อนสถานะ `APPROVED`) |
| | `POST /api/rental-requests/datatable` | ค้นหาและแบ่งหน้ารายการคำขอค่าเช่า |
| **Approvals** | `GET /api/approvals/inbox` | ดึงรายการคำขอที่รอให้ผู้อนุมัติคนปัจจุบันดำเนินการ |
| | `POST /api/approvals/{id}/approve` | อนุมัติคำขอในขั้นปัจจุบัน (หากเป็นขั้นสุดท้ายจะส่งเข้าคิว ERP อัตโนมัติ) |
| | `POST /api/approvals/{id}/return` | ส่งคืนคำขอให้ผู้ขอแก้ไข (บังคับระบุเหตุผล) |
| | `POST /api/approvals/{id}/reject` | ไม่อนุมัติ/ปฏิเสธคำขอ (บังคับระบุเหตุผล) |
| **ERP Sync & Mock** | `POST /api/mock-erp/invoices` | Mock ERP API จำลองการตั้งหนี้ (มีโอกาสสุ่มเกิด Transient Error 30% เพื่อทดสอบระบบ Retry) |
| | `GET /api/erp-monitoring` | ติดตามสถานะการส่งข้อมูลเข้า ERP และประวัติการ Retry |
| | `POST /api/erp-monitoring/{requestId}/retry` | สั่งส่งข้อมูลเข้า ERP ซ้ำแบบ Manual |
| **Master & Admin** | `/api/master/*` | จัดการข้อมูลหลัก (`contract`, `vendor`, `cost-center`, `rental-type`) |
| | `/api/approval-assignments` | ตั้งค่าผูกสายการอนุมัติขั้นที่ 1 (`MANAGER`) และขั้นที่ 2 (`EXECUTIVE`) ให้กับผู้ขอ |
| | `/api/users` | จัดการบัญชีผู้ใช้งานและบทบาท (เฉพาะ `ADMINISTRATOR`) |

---

## 6. สมมติฐานที่ตั้งขึ้นเอง (Design Assumptions)

เนื่องจากโจทย์เปิดโอกาสให้ตั้งสมมติฐานในจุดที่ไม่ได้ระบุรายละเอียดไว้ ระบบจึงออกแบบภายใต้สมมติฐานทางธุรกิจและหลักการควบคุมภายใน (Internal Control) ดังนี้:

1. **เกณฑ์วงเงิน `50,000` บาท คิดจากยอดค่าเช่าก่อนภาษีมูลค่าเพิ่ม (`Amount` ก่อน VAT):**
   - หาก `Amount <= 50,000.00` บาท ใช้การอนุมัติ **1 ขั้น** (เฉพาะหัวหน้า `MANAGER`)
   - หาก `Amount > 50,000.00` บาท ใช้การอนุมัติ **2 ขั้น** (หัวหน้า `MANAGER` อนุมัติขั้นที่ 1 ผู้บริหาร `EXECUTIVE` อนุมัติขั้นที่ 2)
   - อัตราภาษีมูลค่าเพิ่ม (`VatRate`) มีค่าเริ่มต้นที่ `7.00%` (สามารถปรับเปลี่ยนได้ตามประเภทสัญญา เช่น `0%` สำหรับรายการยกเว้น VAT) และคำนวณ `VatAmount` กับ `TotalAmount` ด้วย `DECIMAL(18,2)`

2. **การแบ่งแยกหน้าที่อย่างเคร่งครัด (Segregation of Duties - SoD):**
   - เฉพาะผู้ใช้บทบาท **`REQUESTER` (ผู้ขออนุมัติ)** เท่านั้นที่สามารถสร้าง แก้ไข และส่งใบคำขอเช่าได้
   - ผู้จัดการ (`MANAGER`) และผู้บริหาร (`EXECUTIVE`) ทำหน้าที่ตรวจสอบและอนุมัติเท่านั้น ไม่สามารถสร้างใบคำขอเองหรืออนุมัติคำขอของตนเองได้ 

3. **การป้องกันคำขอซ้ำสำหรับสัญญาเดียวกันและงวดเดียวกัน:**
   - ห้ามมีใบคำขอที่มี `ContractId` + `PeriodStartDate` + `PeriodEndDate` ตรงกันในระบบ ยกเว้นใบคำขอเดิมนั้นอยู่ในสถานะ **`REJECTED` (ปฏิเสธ)** หรือ **`CANCELLED` (ยกเลิก)** แล้ว จึงจะสามารถสร้างคำขอใหม่ในงวดเดียวกันได้

4. **การส่งกลับให้แก้ไข (`REVISION_REQUIRED`) ต้องมีการแก้ไขข้อมูลจริงก่อนส่งใหม่:**
   - เมื่อผู้อนุมัติในขั้นที่ 1 หรือขั้นที่ 2 กดส่งคืนให้แก้ไข (`Return`) สถานะคำขอจะเปลี่ยนเป็น `REVISION_REQUIRED`
   - ผู้ขอจะต้องแก้ไขข้อมูลในฟอร์มหรืออัปเดตเอกสารแนบอย่างน้อย 1 รายการ (ระบบตรวจสอบเทียบกับ Snapshot ของรอบล่าสุด) หากไม่แก้ไขอะไรเลยแล้วกดส่งซ้ำ ระบบจะไม่อนุญาตให้ส่ง
   - เมื่อส่งขออนุมัติใหม่ (`Resubmit`) ระบบจะเพิ่มรอบ `CurrentRevision + 1`, บันทึก JSON Snapshot รอบใหม่ลง `RequestRevisions`, และ **เริ่มเดินสายอนุมัติใหม่ตั้งแต่ขั้นที่ 1 (`MANAGER`) เสมอ** เพื่อให้หัวหน้ารับทราบข้อมูลที่แก้ไขล่าสุดก่อนส่งต่อผู้บริหาร

5. **การยกเลิกใบคำขอ (`Cancel`):**
   - เจ้าของคำขอ (`REQUESTER`) สามารถกดยกเลิกคำขอของตนเองพร้อมระบุเหตุผลได้เฉพาะก่อนที่คำขอจะได้รับการอนุมัติครบถ้วน (`DRAFT`, `PENDING_MANAGER`, `PENDING_EXECUTIVE`, `REVISION_REQUIRED`) หากสถานะเป็น `APPROVED` แล้วจะไม่สามารถยกเลิกผ่านหน้าคำขอได้

6. **กลไกเชื่อมต่อ ERP อัตโนมัติและระบบ Retry:**
   - เมื่อคำขอได้รับอนุมัติครบขั้นตอนสุดท้าย ระบบจะสร้างรายการในตาราง `ErpQueue` และส่งข้อมูลไปยัง Mock ERP API ทันที
   - Mock ERP API (`/api/mock-erp/invoices`) จำลองสถานการณ์จริงโดยมีโอกาสสุ่มตอบกลับ `HTTP 503 Service Unavailable` ประมาณ **30%** (หรือบังคับให้ล้มเหลวได้โดยใส่คำว่า `[FORCE_ERP_FAIL]` ในช่องหมายเหตุ)
   - หากส่งไม่สำเร็จ `ErpBackgroundWorker` จะทำการส่งซ้ำอัตโนมัติแบบ **Exponential Backoff** (`10 วินาที`, `30 วินาที`, `60 วินาที`, `120 วินาที`) สูงสุด **5 ครั้ง** (`MaxAttempts = 5`) และใช้ `IdempotencyKey` (`ERP-REQ-{RequestId}-REV-{RevisionNumber}`) ป้องกันการตั้งหนี้ซ้ำ

7. **การลบข้อมูลแบบ Soft Delete (`Active = 1 / 0`):**
   - ทุกตารางในระบบใช้การลบแบบ Soft Delete (`Active = 0`) ห้ามใช้คำสั่ง `DELETE` ลบแถวข้อมูลจริงออกจากฐานข้อมูล เพื่อรักษาความสมบูรณ์ของ Audit Trail และ Foreign Key ย้อนหลัง 100%

8. **การติดตามรอยการทำงาน (Traceability & Audit Logging):**
   - เก็บทั้ง **Database Audit Log (`dbo.Logs`)** ที่บันทึกทุกการกระทำพร้อมรายละเอียดการแก้ไข (`จาก '...' เป็น '...'`) และ **Application Flow Log (`Logs/api-YYYY-MM-DD.log`)** ที่บันทึกเฉพาะเหตุการณ์สำคัญของ Flow พร้อม `CorrelationId` (`FLOW-REQ-XXXX`), เวลา `INBOUND` / `OUTBOUND`, URL เต็ม, และ Payload

---

## 7. สิ่งที่ยังไม่ได้ทำ / แนวทางพัฒนาต่อยอด (Out of Scope & Future Enhancements)

1. **ระบบแจ้งเตือนทางอีเมล / Microsoft Teams (External Notifications):**
   -
2. **การเชื่อมต่อระบบยืนยันตัวตนขององค์กร (SSO / Entra ID / Active Directory):**
   
3. **การอ่านข้อมูลใบแจ้งหนี้อัตโนมัติด้วย OCR (Document Intelligence):**
   - ปัจจุบันระบบตรวจสอบและจัดเก็บไฟล์ PDF จริงลงโฟลเดอร์ตามเลขที่สัญญา (`Attachment/<ContractNumber>/<RequestId>/`) แต่ยังไม่ได้ใช้ OCR อ่านตัวเลขจากไฟล์ PDF ใบแจ้งหนี้มาเปรียบเทียบกับยอดที่กรอกโดยอัตโนมัติ

4. **การสร้างใบคำขออัตโนมัติรายเดือน (Auto-Draft Recurring Scheduler) และระบบแจ้งเตือนสัญญาใกล้หมดอายุ:**
   - ได้ออกแบบแนวทางไว้ในเอกสาร **Part 4 (Lean Process Improvement)** สำหรับพัฒนาต่อในเฟสถัดไป เพื่อลดภาระการคีย์คำขอรายเดือนที่มีราคาคงที่ตามสัญญา

5. **Automated Unit Tests & Docker Compose:**
   - ปัจจุบันเน้นความครบถ้วนของ End-to-End Workflow (REST API + Full MVC Web UI + Real PDF Storage + ERP Retry Queue + SQL Installer) จึงยังไม่ได้ทำ Unit Tests
