using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ZaloAi.Infrastructure.Persistence;

/// <summary>Lưu enum dạng chữ thường ("active", "owner") để đọc DB trực tiếp vẫn hiểu, và thêm giá trị mới không lệch số.</summary>
public sealed class LowercaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => v.ToString().ToLowerInvariant(),
    v => Enum.Parse<TEnum>(v, true))
    where TEnum : struct, Enum;
