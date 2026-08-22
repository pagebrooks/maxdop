-- Weekly revenue by rep, with a running total.
create procedure dbo.usp_RepRevenue @Start date, @End date as
begin
set nocount on;
with sales as (select r.RepId, r.RepName, sum(o.Total) as Revenue
from dbo.Orders o join dbo.Reps r on r.RepId=o.RepId
where o.OrderDate>=@Start and o.OrderDate<@End and o.Status<>'cancelled'
group by r.RepId,r.RepName)
select s.RepName, s.Revenue,
sum(s.Revenue) over (order by s.Revenue desc rows unbounded preceding) as RunningTotal,
case when s.Revenue>100000 then 'A' when s.Revenue>50000 then 'B' else 'C' end as Tier
from sales s
order by s.Revenue desc;
end
