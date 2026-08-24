create proc dbo.Purge @Min int as
begin
if @Min is null
begin
set @Min = 0;
end
delete from dbo.LittleBobbyTables
where Grade < @Min;
end
