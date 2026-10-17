QT Studio Chapter Detector Demo — M1

Chạy dữ liệu tổng hợp 5.000 chương:
  QTStudio.ChapterDetector.Demo.exe --synthetic 5000

Benchmark nguồn lớn (10.000 chương, khoảng 30 triệu UTF-16):
  QTStudio.ChapterDetector.Demo.exe --synthetic 10000 3000

Đọc file UTF-8 (Auto chỉ chấp nhận UTF-8 hợp lệ):
  QTStudio.ChapterDetector.Demo.exe duong-dan-truyen.txt

Đọc file legacy với bảng mã được chọn rõ:
  QTStudio.ChapterDetector.Demo.exe duong-dan-truyen.txt --encoding gbk
  QTStudio.ChapterDetector.Demo.exe duong-dan-truyen.txt --encoding gb18030

Demo chỉ đọc nguồn và in báo cáo JSON ra console. Nó không mở hoặc ghi Project QT Studio.
