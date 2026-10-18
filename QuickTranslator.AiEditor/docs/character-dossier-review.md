# QT Studio — Character Dossier Review v1 (2026-10-18)

## Mục tiêu UX

Một bộ truyện có 20 nhân vật đã định danh đúng -> **20 hồ sơ duyệt cấp cao nhất**, thay vì hàng trăm
dòng đề xuất quan hệ và xưng hô. Dữ liệu thô/đã duyệt/đã từ chối vẫn tồn tại trong
`ai-book-intelligence.json` và có thể xem ở chế độ **Xem đề xuất thô**.

Màn hình mặc định: **Hồ sơ nhân vật**, một hàng cho mỗi định danh:
- Tên Trung / Việt, bí danh, giới tính và đại từ trần thuật.
- Các quan hệ với nhân vật khác được gom theo người liên quan + loại quan hệ chuẩn hóa.
- Chỉ một form cho từng nhân vật; chọn quan hệ hiển thị luôn loại quan hệ,
  xưng hô hai chiều, mốc chương, evidence gộp từ nhiều batch và cờ xung đột.
- Một quan hệ xuất hiện trong hồ sơ của cả hai người, nhưng **chỉ tính một lần**
  trong tổng số nhóm chờ duyệt.
- Dữ liệu CharacterProfile đã xác nhận và candidate cùng **tên Hán chính xác, duy nhất**
  hiển thị trong một hồ sơ có dấu "mơ hồ" cho đến khi xác nhận gộp. Điều này **không**
  tự merge tài nguyên hay đổi danh tính AI.
- Những tên chỉ được đoán là đối tượng quan hệ mà chưa có Character Candidate /
  CharacterProfile **không** tự biến thành hồ sơ nhân vật. Chúng vẫn có mặt trong quan hệ
  của những nhân vật đã biết và trong tab kỹ thuật. Không nên hiểu số hồ sơ là toàn bộ
  số người thực sự xuất hiện trong nguyên tác.

## Phân biệt dữ liệu được đề xuất và được duyệt

1. **Duyệt nhân vật** — Chọn `Mở hồ sơ nhân vật` -> `Sửa / duyệt hồ sơ nhân vật`.
   Dùng CharacterProfile editor đang có để xác nhận tên/alias/giới tính/đại từ rồi
   commit an toàn qua `BookIntelligenceReviewService`. Nếu đã có cùng tên Trung,
   nút đổi sang `Xác nhận gộp đề xuất AI vào nhân vật đã có`; người dùng phải
   xác nhận trước, không làm tự động.
2. **Duyệt quan hệ** — Chọn một loại quan hệ trong hồ sơ, xem evidence và sửa
   giá trị. Những trường xưng hô/chương chỉ được điền sẵn khi các batch cho **một
   giá trị không mâu thuẫn**; trống không đồng nghĩa sai. Nếu có hai giá trị mâu thuẫn,
   form để trống và cảnh báo. Quan hệ có chiều buộc chọn vai trò được chứng minh.
   Cần có **hai nhân vật đã lưu** trước khi chốt quan hệ: tránh đồ thị tham chiếu
   CandidateIdentity GUID không có Profile chính thức.
3. **Không có nút duyệt tất cả quan hệ bằng một cú nhấp.** AI không đủ chính xác
   để bật những quy tắc xưng hô trên toàn truyện khi chưa có người kiểm tra.
   Duyệt một quan hệ cập nhật dữ liệu bền vững; những kết luận nguồn khác vẫn
   giữ trong lịch sử để đối chiếu.

## Tính bất biến, backward compatibility

- `CharacterDossierReviewService.Build` là phép chiếu **read-only** từ
  `BookIntelligenceState` + `ProjectResources`: không thay đổi schema JSON,
  không xóa/ghi đè bất kỳ đề xuất gốc, chứng cứ, xưng hô đã duyệt hoặc checkpoint.
- Không cần gọi Gemini lại để sử dụng giao diện mới với Project 445 chương.
- Kiểm tra định danh bằng GUID, tên Trung chính xác và alias *không xung đột*.
  Bí danh trùng nhiều người phải gắn mơ hồ, không tự gộp để có số hồ sơ đẹp.
- Các trường gợi ý `我/女友/男友` bị bỏ khỏi bản nháp xưng hô có thể xác nhận,
  nhưng được giữ trong đề xuất gốc/Reason để đối chiếu.
- Bộ lọc FastAdaptive và handling provider-block không thay đổi.

## Test / đóng gói

```powershell
dotnet test QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/QuickTranslator.AiEditor.Tests.csproj -c Release
dotnet test QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Wpf.Tests/QuickTranslator.AiEditor.Wpf.Tests.csproj -c Release
```

Chạy thêm UI smoke test: chế độ mặc định là hồ sơ, toggle sang đề xuất thô rồi
quay lại, mở hồ sơ có 10–20 quan hệ, chọn quan hệ có/không có xưng hô, xác nhận
nhân vật đã có và quan hệ 2 chiều; tất cả không cần gọi API thật.

Các test mới trong `CharacterDossierReviewTests` gồm 20 nhân vật / 263
claims, alias trùng, approved-profile trùng candidate, conflict timeline,
consensus xưng hô.

## Giới hạn / roadmap

Đây là **Character-centric review model** trên dữ liệu trích xuất từng batch,
chưa phải model AI được huấn luyện để trả JSON duy nhất cho cả cuốn. Hệ thống
vẫn cần benchmark precision/recall danh tính trước khi khẳng định đã tìm được
đầy đủ số nhân vật. Bước tiếp theo là Character Dossier consolidate/merge
xuyên chương được AI xác minh có dẫn evidence, thay vì gom trùng dựa tên.
