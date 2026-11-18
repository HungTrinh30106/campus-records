using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;

namespace StudentRecords.Api;

public static class Seed
{
    public static async Task Run(IServiceProvider services, IConfiguration config, IHostEnvironment env)
    {
        if (!config.GetValue<bool>("Seed:Enabled"))
            return;
        if (!env.IsDevelopment())
            throw new InvalidOperationException("Seed chỉ được chạy trong Development.");
        var password = config["Seed:Password"];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Seed:Password phải được cung cấp bên ngoài source.");
        var db = services.GetRequiredService<Database>();
        if (await db.Users.Find(x => x.Role == "Admin").AnyAsync())
            return;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var deps = new[]
        {
            new Department
            {
                Code = "CNTT",
                Name = "Công nghệ thông tin"
            },
            new Department
            {
                Code = "KINHTE",
                Name = "Kinh tế"
            },
            new Department
            {
                Code = "NGONNGU",
                Name = "Ngôn ngữ"
            }
        };
        foreach (var d in deps)
        {
            var existing = await db.Departments.Find(x => x.Code == d.Code).FirstOrDefaultAsync();
            if (existing != null)
                d.Id = existing.Id;
            else
                await db.Departments.InsertOneAsync(d);
        }

        string[] names = ["Nguyễn Minh Anh", "Trần Hoàng Nam", "Lê Thu Hà", "Phạm Đức Huy", "Hoàng Ngọc Linh", "Vũ Quang Minh", "Đặng Phương Thảo", "Bùi Gia Bảo", "Đỗ Khánh Vy", "Hồ Tuấn Kiệt", "Ngô Hồng Nhung", "Dương Nhật Long", "Lý Thanh Mai", "Trịnh Anh Khoa", "Đinh Thùy Dương", "Mai Quốc Việt", "Tạ Bảo Châu", "Cao Hải Đăng"];
        var rows = new List<Student>();
        for (var i = 0; i < 18; i++)
        {
            var row = new Student
            {
                StudentCode = $"SV{2023001 + i}",
                FullName = names[i],
                Email = $"sinhvien{i + 1}@example.edu.vn",
                Phone = $"090{1234500 + i}",
                NationalId = $"079{123456780 + i}",
                DateOfBirth = $"200{2 + i % 3}-{1 + i % 12:00}-{5 + i % 20:00}",
                Gender = i % 2 == 0 ? "Female" : "Male",
                Address = $"{12 + i * 3} Nguyễn Văn Cừ, TP. Hồ Chí Minh",
                DepartmentId = deps[i / 6].Id,
                ClassName = $"{deps[i / 6].Code}23{(char)('A' + i % 3)}",
                EnrollmentYear = 2023 + i % 2,
                Status = i % 7 == 0 ? "Paused" : i % 8 == 0 ? "Graduated" : "Active",
                Gpa = Math.Round(2.5 + i % 15 / 10.0, 1),
                CreatedAt = DateTime.UtcNow.AddDays(-i - 1),
                UpdatedAt = DateTime.UtcNow.AddDays(-i)
            };
            var existing = await db.Students.Find(x => x.StudentCode == row.StudentCode).FirstOrDefaultAsync();
            if (existing != null)
                row = existing;
            else
                await db.Students.InsertOneAsync(row);
            rows.Add(row);
        }

        foreach (var u in new[]
        {
            new AppUser
            {
                FullName = "Quản trị viên",
                Email = "admin@campus.local",
                Role = "Admin"
            },
            new AppUser
            {
                FullName = "Nguyễn Thị Hạnh",
                Email = "staff@campus.local",
                Role = "Staff",
                DepartmentId = deps[0].Id
            },
            new AppUser
            {
                FullName = rows[0].FullName,
                Email = "student@campus.local",
                Role = "Student",
                StudentId = rows[0].Id
            }
        }

        )
        {
            if (await users.FindByEmailAsync(u.Email) != null)
                continue;
            var result = await users.CreateAsync(u, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Không thể seed: mật khẩu phải thỏa chính sách Identity.");
        }
    }
}
