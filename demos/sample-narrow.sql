-- Revenue by rep, banded.
create procedure dbo.usp_Revenue @Start date as
begin
set nocount on;
select r.RepName,sum(o.Total) as Revenue,
case when sum(o.Total)>100000 then 'A'
when sum(o.Total)>50000 then 'B'
else 'C' end as Tier
from dbo.Orders o
join dbo.Reps r on r.RepId=o.RepId
where o.OrderDate>=@Start
group by r.RepName order by Revenue desc;
end
