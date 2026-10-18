# QT Studio — Book Intelligence: relationship trust gate (2026-10-18)

## Vấn đề được xử lý
Bản thử nghiệm 445 chương có 263 đề xuất chờ duyệt và các lỗi: quan hệ bị trùng theo cặp,
nhãn vợ/chồng/boyfriend chưa chuẩn, định hướng cha-con có thể bị đảo, đầu ra `我/女友/男友`
bị đưa vào ô xưng hô, kiểm chứng AI chỉ dựa trên tên xuất hiện.

## Pipeline mới trong lượt khảo sát
1. **Character Intelligence (candidate memory):** Bộ nhớ ứng viên và alias thu được từ các batch
   trước được đưa vào prompt nếu tên hoặc alias xuất hiện trong batch hiện tại. Nhân vật chưa duyệt
   được dán nhãn rõ là *ứng viên*, KHÔNG tự đưa vào tài nguyên nhân vật chính thức.
2. **Relationship Intelligence (quality gate):** Tất cả các nhận định quan hệ được kiểm tra xem
   hai tên có cùng xuất hiện trong ít nhất một chứng cứ và có từ khóa quan hệ phù hợp hay không.
   Chỉ là tín hiệu để xem xét, không phải bằng chứng ngữ nghĩa kết luận. Các kết luận có chiều
   (`Parent`, `Child`, `Teacher`, `Superior`...) bị chuyển `ReviewLater` cho đến khi
   người duyệt chọn rõ ai ở vai trò nào. **Không tạo graph fact trực tiếp từ AI**; chỉ tạo khi người
   dùng phê duyệt. Các nhãn chưa có trong từ điển không được tự biến thành graph fact.
3. **Addressing Intelligence (review-only):** Chưa thể suy ra xưng hô hai chiều chỉ từ nhãn
   quan hệ. Các giá trị đầu ra dạng tiếng Hán, danh từ vai trò tình cảm hoặc đại từ phi ngữ cảnh
   bị loại khỏi *bản nháp rule dùng khi dịch*, nhưng giá trị gốc được ghi chú để đối chiếu.
   Lưu rule chỉ với cặp tự xưng/gọi người nghe hợp lệ do người dùng duyệt.

### Hợp nhất dữ liệu nhưng không làm mất lịch sử
- **Trên ingestion mới**, cùng cặp, cùng loại quan hệ chuẩn hóa chỉ tạo một đề xuất chờ duyệt;
  chứng cứ các batch được gộp. Những xưng hô khác nhau được lưu dưới dạng ghi chú mâu thuẫn,
  không ghi đè một trong hai.
- **Với dữ liệu cũ**, nút `Hồ sơ theo cặp` tập hợp những đề xuất trước đó thành một hồ sơ
  (kể cả xung đột / mốc chương), rồi có thể mở lại từng đề xuất để duyệt riêng. Không xóa
  hoặc thay đổi quyết định Accepted/Rejected trước đây.
- Snapshot giao tiếp dịch giữ nguyên dữ liệu đã xác nhận, nhưng loại bỏ **quan hệ có chiều**
  trong graph cũ mà nguồn còn là `DirectAi` (chưa qua bước xác nhận hướng người duyệt mới)
  và các quy tắc addressing không hợp lệ. Những mục đó vẫn ở file để đối chiếu.
- Giữ nguyên schema JSON (thêm trường `ConfirmedDirection` với mặc định Unverified); không
  xóa checkpoint, không đặt lại `AiAnalyzed`, không gọi lại API tự động.
- Thay đổi phiên bản prompt/schema để **các batch chưa phân tích** dùng phiên bản mới;
  muốn khảo sát lại toàn bộ nguyên tác phải nhấn `Khảo sát lại` sau khi sao lưu.

## Giới hạn chưa giải quyết
- Đây là hệ thống **kiểm soát chất lượng hậu trích xuất**, chưa phải 3 lần gọi AI riêng
  cho character / relationship / addressing. Mỗi batch vẫn dùng 1 schema JSON.
- Kiểm tra cùng đoạn + tín hiệu quan hệ **không chứng minh** kết luận đúng; mọi quan hệ đều
  đòi hỏi duyệt thủ công. Các quan hệ gián tiếp (đại từ, alias bị thiếu) sẽ được gắn ReviewLater.
- Bộ lọc từ vựng xưng hô bảo thủ có thể bỏ qua cách gọi hợp lệ bằng Hán ngữ; dữ liệu gốc
  được giữ lại trong `Reason`. Cần so sánh nguyên tác trước khi sửa tay.
- Không có tự động chuẩn hóa quan hệ theo xuyên chương / timeline phức tạp khi không có
  chứng cứ rõ. Không tăng batch hoặc tự gọi lại model bị provider chặn.

## Test / chạy thử

Chạy trên Windows máy có .NET 10:

```powershell
dotnet test QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/QuickTranslator.AiEditor.Tests.csproj -c Release
dotnet test QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Wpf.Tests/QuickTranslator.AiEditor.Wpf.Tests.csproj -c Release
```

Các test mới: `RelationshipIntelligencePhaseTests` và smoke test nút `Hồ sơ theo cặp`.
Kiểm tra trường hợp cũ có knowledge graph / xưng hô Accepted và dữ liệu chưa duyệt.
Không cần Gemini API thật để test các rule cốt lõi.

## Bài benchmark tiếp theo
Dùng dữ liệu hiện có (đúng file TXT 445 chương và JSON khảo sát) để đo riêng:
- AI phát hiện đúng và bỏ sót nhân vật nào;
- Quan hệ bao nhiêu cặp duy nhất, bao nhiêu giả thuyết không cùng chứng cứ;
- Bao nhiêu quan hệ có chiều chưa xác nhận, bao nhiêu addressing không dùng được;
- Bao nhiêu evidence AI xử lý thành công / blocked / FastAdaptive bỏ qua.
